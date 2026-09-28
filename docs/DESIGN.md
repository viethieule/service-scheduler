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
   technicians, service types) inside it. Integrating with external dealership
   systems on the booking path is not realistic, and is out of scope.
2. **Vehicles live inside our system too**, owned by the customer and pre-registered. A
   booking references a `vehicleId`; VIN lookup and vehicle onboarding are out of scope.

## 3. Technology Choices

**.NET and ASP.NET Core Web API.** This is the stack I know best, and I picked it so that the
effort in this exercise goes into the scheduling problem rather than into learning a framework.
Nothing in the design depends on it; the parts that carry the invariant live in the database.

**PostgreSQL.** This one is not a default. I chose it for the `EXCLUDE` constraint over a
`btree_gist` index, which lets the no-overlap invariant (NFR 5.1) be stated once,
declaratively, at the data layer, instead of being re-implemented in every code path that
writes an appointment. That makes this a deliberately technology-led decision: the hardest
requirement — never double-booking a bay or a technician under concurrency — drove the choice
of database, rather than the database being picked first and the requirement bent to fit it.
Starting from a store without range exclusion, correctness would have had to be maintained by
application code, which is exactly what NFR 5.1 says it must not depend on.

**Concurrency control: optimistic first, pessimistic only where it earns its place.** My
preference is to reach for optimistic concurrency at the high level and to add pessimistic
locking only where measurement or reasoning shows it is needed, rather than serialising by
reflex. The code follows that order literally. The exclusion constraints came first and are
always on: they are the floor, they cost nothing until a conflict actually occurs, and they
protect every writer. The dealership-day advisory lock was added afterwards, on top, and only
once the cost of the optimistic-only approach was understood — the loser of a race was
rejected even when a second bay was free, so the system reported no capacity while capacity
existed. The lock exists to recover that wasted capacity, not to establish correctness; remove
it and utilisation degrades, but the data cannot be corrupted.

**EF Core used directly in the service layer.** `BookingService` takes the `DbContext` and
treats it as the unit of work. There is no repository interface in front of it, because EF
Core already is one: `DbSet<T>` is a repository and `SaveChangesAsync` is a unit-of-work
commit. Wrapping them would add a layer that only forwards calls, and it would obscure the two
things this design depends on most — the advisory lock and the exclusion-violation SQLSTATE —
both PostgreSQL specific, and both of which would leak through such an abstraction anyway. The
trade is that the service layer is tied to EF Core. I accept it, and state the persistence
exception openly rather than hide it behind a pretence of portability.

## 4. Functional Requirements

1. **Resource Constrained Booking**: Allow a user to request a service appointment for a
   specific vehicle, service type, and dealership at a desired time.
2. **Real-Time Availability Check**: Before confirming, check for the availability of
   both a ServiceBay and a qualified Technician for the entire service duration.
3. **Confirmed Appointment Record**: Upon success, create a persistent Appointment record
   associating the customer, vehicle, technician, and service bay.

## 5. Non-Functional Requirements

### 5.1 Consistency (highest priority)

No service bay or technician may ever be assigned to two overlapping appointments — a
hard invariant enforced at the data layer, not by application-level checks. Availability
reads are therefore **advisory** (a best-effort snapshot), while the booking confirm is
**authoritative and atomic**, failing a lost race with a clean conflict.

### 5.2 Observability

Structured logs with a correlation id across the booking flow, plus booking funnel metrics
(availability → attempt → confirmed / conflicted / rejected). Conflict rate is a
first-class signal: a rising rate means slot granularity or the concurrency strategy is
wrong.

### 5.3 Out of scope

Acknowledged but excluded to keep scope on preventing overlapping bookings: dealership
operating hours and technician roster calendars, timezone and daylight-saving handling,
request idempotency, fail-closed degradation, injectable clock for testability, audit
trail, authorisation and tenant security, durability of downstream effects, and
data-driven scheduling policy.

## 6. Core Entities

| Entity | Columns |
| --- | --- |
| `Dealership` | `id`, `name`, `address` |
| `ServiceBay` | `id`, `dealership_id`, `name` |
| `Technician` | `id`, `dealership_id`, `name` |
| `ServiceType` | `id`, `name`, `duration_minutes` |
| `Customer` | `id`, `name`, `email`, `phone` |
| `Vehicle` | `id`, `customer_id`, `vin`, `make`, `model`, `year`, `fuel_type` |
| `Appointment` | `id`, `dealership_id`, `customer_id`, `vehicle_id`, `service_type_id`, `technician_id`, `service_bay_id`, `start_at`, `end_at`, `status`, `created_at` |

Notes:

- `Appointment` carries the consistency invariant (NFR 5.1): no two active appointments may
  overlap on the same `technician_id`, nor on the same `service_bay_id`.
- Availability is determined **solely** by the absence of an overlapping appointment — a
  resource is available whenever it is not already booked. Opening hours and technician
  rosters are out of scope (NFR 5.3).
- All times are UTC — `start_at`, `end_at`, the requested `date` and the returned slots.
  Per-dealership local time is out of scope (NFR 5.3).

## 7. API Endpoints

Both endpoints are owned by a single service (section 8.1).

```
GET  /availability?serviceTypeId=&date=
POST /bookings
```

### 7.1 `GET /availability`

Answers **"where and when can I bring the car in?"** — it returns candidate dealerships, each
with its bookable start times. Service bays and technicians are capacity that gates whether a
time is offered; they are never exposed to the customer and never chosen by them.

A start time is offered when, for `window = [start, start + duration)`, the dealership has at
least one service bay free for the whole window **and** at least one technician free for the
whole window — checked independently, since a bay and a technician are paired only at booking. Free means no overlapping appointment. 

Assumption:
Candidate start times are stepped on a 30-minute grid.

Request:

| Parameter | Required | Notes |
| --- | --- | --- |
| `serviceTypeId` | yes | determines the appointment duration |
| `date` | yes | a single day |

```
GET /availability?serviceTypeId=svc_brake&date=2026-10-02
```

Response `200`:

```json
{
  "serviceTypeId": "svc_brake",
  "durationMinutes": 120,
  "date": "2026-10-02",
  "slotGranularityMinutes": 30,
  "dealerships": [
    { "dealershipId": "dlr_123", "name": "Northside Motors",
      "slots": ["08:00", "08:30", "13:00"] },
    { "dealershipId": "dlr_777", "name": "Riverside Auto",
      "slots": ["09:00", "15:30"] }
  ]
}
```

Dealerships are ordered by name; one with no free slots is omitted rather than returned empty.

| Status | Meaning |
| --- | --- |
| `200 OK` | possibly an empty `dealerships` array — a valid answer, not an error |
| `422 Unprocessable` | unknown `serviceTypeId`, malformed or past `date` |

### 7.2 `POST /bookings`

Request:

```json
{
  "dealershipId": "dlr_123",
  "vehicleId": "veh_456",
  "serviceTypeId": "svc_brake",
  "startAt": "2026-10-02T09:00:00Z"
}
```

The client requests a **place and a time**, never a technician or a bay — the server selects and
reserves those, so the client cannot propose an invalid pairing or hold a stale assignment.
`end_at` is derived from the service type's duration.

| Status | Meaning |
| --- | --- |
| `201 Created` | Appointment confirmed; body includes the assigned `technicianId` and `serviceBayId` |
| `409 Conflict` | No bay or no technician free for the whole duration |

## 8. High-Level Design

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
               |
               v
  +-----------------------------+
  |       Booking Service       |
  |   availability - bookings   |
  +-----------------------------+
               |
               v
  +-----------------------------+
  |     Database (PostgreSQL)   |
  +-----------------------------+
```

### 8.1 Components

- **API Gateway** — single entry point: routing, authentication, rate limiting. Keeps
  cross-cutting concerns out of the service.
- **Booking Service** — owns `Appointment`, and therefore availability computation and the
  write path. This is where the consistency invariant (NFR 5.1) is enforced.
- **Database** — one PostgreSQL instance.

### 8.2 Booking request flow

The customer never selects a technician or a bay. They choose *what*, *when* and *where*; the
server chooses *who* and *which*.

**1 — Select vehicle.** From the customer's garage. Fixes `vehicleId` (FR 1).

**2 — Select service type.** Fixes the appointment's `durationMinutes`.

**3 — Select a date.** A single day.

**4 — Read availability.** `GET /availability?serviceTypeId=&date=` returns each dealership with
its bookable start times (7.1). The customer sees places and times only. **Advisory** — this
call reserves nothing.

**5 — Pick a dealership and a time** from the returned set.

**6 — Submit the booking.** `POST /bookings` with `dealershipId` and the chosen `startAt`.

**7 — Server confirms atomically.** In one transaction the Booking Service:

1. resolves `duration` from the service type and computes `end_at`;
2. re-evaluates availability for `[start_at, end_at)` — the authoritative check;
3. picks one free bay and one free technician;
4. inserts the `Appointment`, with the database rejecting any overlap on either resource.

On success `201` with the assigned `technicianId` and `serviceBayId`. On a lost race `409`, and
the customer returns to step 4 with a refreshed slot list.

Note the ordering: the dealership is now an **output of step 4**, not an input. FR 1 still holds
— `POST /bookings` names a specific dealership — but the customer discovers it by availability
rather than choosing it up front.

Steps 4 and 7 both perform the availability check, and that duplication is deliberate: step 4
is a fast, cacheable, advisory read that makes the UI usable; step 7 is the transactional one
that actually upholds NFR 5.1. Only the second is trusted.

## 9. Use of Generative AI

I used Claude throughout the design phase, and it is worth being specific about how, because
"AI-assisted" on its own says nothing.

It was useful in two roles. The first was as a sounding board on domain knowledge I did not
already hold in detail — dealership servicing workflows, and in particular the PostgreSQL
mechanics of enforcing a no-overlap invariant. The `EXCLUDE` constraint with `btree_gist` came
out of that conversation, as did two details I would have been slow to reach alone: that the
invariant needs two separate constraints rather than one, since a combined constraint only
rejects rows matching on both bay *and* technician and would let a technician be booked twice
in different bays; and that the range must be half-open so back-to-back appointments do not
falsely collide. The second role was drafting — turning a decision I had already made into
prose, a migration or a test.

Every structural decision here is mine. Dealership-as-tenant and customer-owned vehicles were
chosen after I worked through who plausibly owns each piece of data. The collapse from two
services to one came from my noticing that the Resource Service had nothing left to own once
availability returned dealership identity inline. Integer identity keys, four projects, clean
architecture with an explicit exception for persistence, and EF Core as the unit of work were
all specified by me.

The pattern I held to most consistently was cutting scope. The model's instinct is a richer
domain model than the requirements need, and I repeatedly removed things: operating hours,
technician shifts and certifications, `is_active` flags, per-dealership timezones and
daylight-saving handling. `ServiceType` went the other way, from entity to enum and back, but
only once duration gave it a reason to exist. I also edited this document by hand to simplify
it and then had the model reconcile the cross-references it had left dangling. The README's
*AI Collaboration Narrative* covers the verification side in more detail.
