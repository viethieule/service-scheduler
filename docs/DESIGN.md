# Unified Service Scheduler — System Design

## 1. Context

In the **Ownership** domain — the post-purchase relationship between a customer and their
vehicle — servicing is the main recurring touchpoint. Most dealerships still book it
manually: phone calls and diaries, with an advisor reconciling by hand which bay is free
and which technician is qualified. It is slow, error-prone, unavailable out of hours, and
double-books resources.

The **Unified Service Scheduler** replaces this with one application that lets a vehicle
owner book directly, guaranteeing every confirmed appointment has a service bay and a
qualified technician reserved for its full duration — under one scheduling model shared
across all dealerships on the platform.

## 2. Assumptions

1. **Dealership is a tenant** of our system and owns its resource data (service bays,
   technicians, skills, operating hours) inside it. Integrating with external dealership
   systems on the booking path is not realistic, and is out of scope.
2. **Vehicles live inside our system too**, owned by the customer and pre-registered. A
   booking references a `vehicleId`; VIN lookup and vehicle onboarding are out of scope.

## 3. Functional Requirements

1. **Resource Constrained Booking**: Allow a user to request a service appointment for a
   specific vehicle, service type, and dealership at a desired time.
2. **Real-Time Availability Check**: Before confirming, check for the availability of
   both a ServiceBay and a qualified Technician for the entire service duration.
3. **Confirmed Appointment Record**: Upon success, create a persistent Appointment record
   associating the customer, vehicle, technician, and service bay.

## 4. Non-Functional Requirements

### 4.1 Consistency (highest priority)

No service bay or technician may ever be assigned to two overlapping appointments — a
hard invariant enforced at the data layer, not by application-level checks. Availability
reads are therefore **advisory** (a best-effort snapshot), while the booking confirm is
**authoritative and atomic**, failing a lost race with a clean conflict.

### 4.2 Read scalability and performance

The workload is read-skewed — customers browse many dealerships and dates before booking
once — and each availability query is an expensive interval search. Because 4.1 makes
these reads advisory they can be cached or served from replicas, and the workload
partitions cleanly by dealership for near-linear horizontal scale.

### 4.3 Observability

Structured logs with a correlation id across the booking flow, plus booking funnel metrics
(availability → attempt → confirmed / conflicted / rejected). Conflict rate is a
first-class signal: a rising rate means slot granularity or the concurrency strategy is
wrong.

### 4.4 Out of scope

Acknowledged but excluded to keep scope on preventing overlapping bookings: dealership
operating hours and technician roster calendars, timezone and daylight-saving handling,
request idempotency, fail-closed degradation, injectable clock for testability, audit
trail, authorisation and tenant security, durability of downstream effects, and
data-driven scheduling policy.

## 5. Core Entities

`ServiceType` is an enum, not a table:

```
ServiceType = OIL_CHANGE | TYRE_CHANGE | DIAGNOSTIC | BRAKE_SERVICE | ANNUAL_SERVICE
```

Its duration is a constant map in the domain layer (`OIL_CHANGE` 60 min, `TYRE_CHANGE` 60,
`DIAGNOSTIC` 90, `BRAKE_SERVICE` 120, `ANNUAL_SERVICE` 180). Making durations configurable
per dealership is out of scope (NFR 4.4).

| Entity | Columns |
| --- | --- |
| `Dealership` | `id`, `name`, `timezone` (IANA), `address`, `is_active` |
| `ServiceBay` | `id`, `dealership_id`, `name`, `is_active` |
| `Technician` | `id`, `dealership_id`, `name`, `certified_service_types` (ServiceType[]), `is_active` |
| `Customer` | `id`, `name`, `email`, `phone` |
| `Vehicle` | `id`, `customer_id`, `vin`, `make`, `model`, `year`, `fuel_type` |
| `Appointment` | `id`, `dealership_id`, `customer_id`, `vehicle_id`, `service_type`, `technician_id`, `service_bay_id`, `start_at`, `end_at`, `status`, `created_at` |

Notes:

- `Appointment` carries the consistency invariant (NFR 4.1): no two active appointments may
  overlap on the same `technician_id`, nor on the same `service_bay_id`.
- Availability is determined **solely** by the absence of an overlapping appointment — a
  resource is available whenever it is not already booked. Opening hours and technician
  rosters are out of scope (NFR 4.4).
- **Qualified** (FR 2) means the requested `ServiceType` is in the technician's
  `certified_service_types`. Bays are interchangeable; they have no capability constraint.
- Storage convention (not a requirement): `start_at` / `end_at` are UTC instants and
  `Dealership.timezone` is retained, so local-time display can be added later without a
  migration of the appointment table.

## 6. API Endpoints

### 6.1 Resource Service

```
GET /dealerships        list dealerships for the user to pick from
```

Static reference data — no appointment state, so it is freely cacheable (NFR 4.2).

### 6.2 Booking Service

```
GET  /availability?dealershipId=&serviceType=&date=
POST /bookings
```

Both are owned by the Booking Service because both read `Appointment`. The flat path (rather
than nesting under `/dealerships/{id}`) keeps that ownership visually obvious.

#### `GET /availability`

Answers **"when can I bring the car in?"** — it returns bookable start times, not resources.
Service bays and technicians are capacity that gates whether a time is offered; they are
never exposed to the customer and never chosen by them.

Request:

| Parameter | Required | Notes |
| --- | --- | --- |
| `dealershipId` | yes | the customer has already chosen where to go (FR 1) |
| `serviceType` | yes | determines duration and the required certification |
| `date` | yes | a single day, in the dealership's local date |

```
GET /availability?dealershipId=dlr_123&serviceType=BRAKE_SERVICE&date=2026-10-02
```

Response `200`:

```json
{
  "dealershipId": "dlr_123",
  "serviceType": "BRAKE_SERVICE",
  "durationMinutes": 120,
  "date": "2026-10-02",
  "slotGranularityMinutes": 30,
  "slots": ["08:00", "08:30", "09:00", "13:00", "13:30", "14:00"]
}
```

| Status | Meaning |
| --- | --- |
| `200 OK` | slot list, possibly empty — an empty list is a valid answer, not an error |
| `404 Not Found` | unknown `dealershipId` |
| `422 Unprocessable` | unknown `serviceType`, malformed or past `date` |

#### 6.2.1 How a slot is decided

A candidate start time is offered if, for `window = [start, start + duration)`:

> there exists **at least one** service bay at that dealership free for the whole window,
> **and** there exists **at least one** technician at that dealership certified for the
> requested service type and free for the whole window.

This is FR 2 stated precisely. Two points that matter:

- The two quantifiers are **independent**. Bay *A* free with technician *X*, and bay *B* free
  with technician *Y*, both satisfy the rule. No matched pair is computed at read time —
  pairing happens only at booking.
- Freedom means *no overlapping appointment*. Per NFR 4.4 there are no opening-hour or roster
  calendars, so a resource is free unless booked.

Algorithm:

```
duration    = DURATION[serviceType]                   // domain constant
candidates  = BUSINESS_DAY start times on `date`, stepped by SLOT_GRANULARITY
bays        = active bays at dealership
technicians = active technicians at dealership where serviceType in certified_service_types
booked      = appointments at dealership overlapping `date`    // one query

for start in candidates:
    window = [start, start + duration)
    if window.end > BUSINESS_DAY.end:             continue
    if no bay in bays is free for window:         continue
    if no tech in technicians is free for window: continue
    emit start
```

The day's appointments are loaded in a **single query** and the grid is evaluated in memory,
so the endpoint costs one database round trip regardless of slot count.

#### 6.2.2 Constants and their consequences

- `SLOT_GRANULARITY = 30 minutes`. This is the knob trading UX precision against contention:
  a finer grid offers more start times but concentrates more customers onto the same capacity,
  raising the conflict rate (NFR 4.3).
- `BUSINESS_DAY = 08:00-18:00`, a fixed placeholder. Per-dealership opening hours are out of
  scope (NFR 4.4); without this constant the candidate grid would span midnight to midnight
  and offer 03:00 appointments. A slot is dropped if the service would not finish by
  `BUSINESS_DAY.end`.
- Durations are a domain-layer constant map keyed by `ServiceType` (section 5).

#### 6.2.3 Advisory, by construction

The response takes **no locks and creates no holds**. A returned slot may be gone before the
customer clicks it; that is expected, and is resolved by `POST /bookings` returning `409`
(NFR 4.1). Introducing holds would trade this benign race for expiry handling, abandoned-hold
cleanup and a second source of truth about capacity — deliberately not done.

#### `POST /bookings`

Request:

```json
{
  "dealershipId": "dlr_123",
  "vehicleId": "veh_456",
  "serviceType": "BRAKE_SERVICE",
  "startAt": "2026-10-02T09:00:00Z"
}
```

The client requests a **time**, never a technician or a bay — the server selects and reserves
those, so the client cannot propose an invalid pairing or hold a stale assignment. `end_at` is
derived from the service type's duration.

| Status | Meaning |
| --- | --- |
| `201 Created` | Appointment confirmed; body includes the assigned `technicianId` and `serviceBayId` |
| `409 Conflict` | No bay or no qualified technician free for the whole duration |
| `422 Unprocessable` | Unknown service type, or vehicle not owned by the caller |

## 7. High-Level Design

Diagram: [high-level-design.excalidraw](high-level-design.excalidraw) — open at
[excalidraw.com](https://excalidraw.com) via *Open*, or with the Excalidraw VS Code extension.

```
            (User)
               |
               v
  +-----------------------------+
  |        API Gateway          |
  |  routing - auth - ratelimit |
  +-----------------------------+
         |              |
         v              v
  +-------------+  +------------------+
  |   Booking   |  |    Resource      |
  |   Service   |  |    Service       |
  +-------------+  +------------------+
         |              |
         v              v
  +-----------------------------+
  |     Database (PostgreSQL)   |
  +-----------------------------+
```

### 7.1 Components

- **API Gateway** — single entry point: routing, authentication, rate limiting. Keeps
  cross-cutting concerns out of the services.
- **Resource Service** — static reference data. Stateless and cacheable. Thin at this stage
  (one endpoint); it is the seam that grows as catalog features land.
- **Booking Service** — owns `Appointment`, and therefore both availability computation and
  the write path. This is where the consistency invariant (NFR 4.1) is enforced.
- **Database** — one PostgreSQL instance for both services at this stage.

### 7.2 Why split this way

The two services have opposite characteristics. Reference data is read-heavy, cacheable and
never changes state. Booking is transactional and contended. Splitting them keeps the
invariant-bearing surface small.

Availability sits on the **Booking** side deliberately. It is derived from `Appointment`, so
placing it in the Resource Service would either read across a service boundary or duplicate
the overlap rule — and NFR 4.1 requires exactly one authoritative definition of it.
Availability also invalidates on every booking, so it does not belong behind the same cache as
static data.

They deliberately share one database for now: availability needs bays, technicians and
existing appointments in a single query, and the booking commit must be one local transaction.
Separate databases would force a replicated read model — unjustified complexity at this stage.

### 7.3 Booking request flow

The customer never selects a technician or a bay. They choose *where*, *what* and *when*; the
server chooses *who* and *which*.

**1 — Select vehicle.** From the customer's own garage. Fixes `vehicleId` (FR 1).

**2 — Select service type.** From the `ServiceType` enum. This fixes two things at once: the
`durationMinutes` of the appointment, and which technicians count as qualified.

**3 — Select dealership.** `GET /dealerships` (Resource Service). The dealership is an input,
not a search result — the customer has to physically deliver the car, so location is a hard
constraint, and FR 1 names it as a given.

**4 — Select a date.** A single day.

**5 — Read availability.** `GET /availability?dealershipId=&serviceType=&date=` (Booking
Service) returns the bookable start times for that day (6.2.1). The customer sees times only.
**Advisory** — this call reserves nothing.

**6 — Submit the booking.** `POST /bookings` with the chosen `startAt`.

**7 — Server confirms atomically.** In one transaction the Booking Service:

1. resolves `duration` from `serviceType` and computes `end_at`;
2. re-evaluates availability for `[start_at, end_at)` — the authoritative check;
3. picks one free bay and one free qualified technician;
4. inserts the `Appointment`, with the database rejecting any overlap on either resource.

On success `201` with the assigned `technicianId` and `serviceBayId`. On a lost race `409`, and
the customer returns to step 5 with a refreshed slot list.

Steps 5 and 7 both perform the availability check, and that duplication is deliberate: step 5
is a fast, cacheable, advisory read that makes the UI usable; step 7 is the transactional one
that actually upholds NFR 4.1. Only the second is trusted.
