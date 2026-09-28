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

### 4.2 Observability

Structured logs with a correlation id across the booking flow, plus booking funnel metrics
(availability → attempt → confirmed / conflicted / rejected). Conflict rate is a
first-class signal: a rising rate means slot granularity or the concurrency strategy is
wrong.

### 4.3 Out of scope

Acknowledged but excluded to keep scope on preventing overlapping bookings: dealership
operating hours and technician roster calendars, timezone and daylight-saving handling,
request idempotency, fail-closed degradation, injectable clock for testability, audit
trail, authorisation and tenant security, durability of downstream effects, and
data-driven scheduling policy.

## 5. Core Entities

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

- `Appointment` carries the consistency invariant (NFR 4.1): no two active appointments may
  overlap on the same `technician_id`, nor on the same `service_bay_id`.
- Availability is determined **solely** by the absence of an overlapping appointment — a
  resource is available whenever it is not already booked. Opening hours and technician
  rosters are out of scope (NFR 4.3).
- All times are UTC — `start_at`, `end_at`, the requested `date` and the returned slots.
  Per-dealership local time is out of scope (NFR 4.3).

## 6. API Endpoints

Both endpoints are owned by a single service (section 7.1).

```
GET  /availability?serviceTypeId=&date=
POST /bookings
```

### 6.1 `GET /availability`

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

### 6.2 `POST /bookings`

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

### 7.1 Components

- **API Gateway** — single entry point: routing, authentication, rate limiting. Keeps
  cross-cutting concerns out of the service.
- **Booking Service** — owns `Appointment`, and therefore availability computation and the
  write path. This is where the consistency invariant (NFR 4.1) is enforced.
- **Database** — one PostgreSQL instance.

### 7.2 Booking request flow

The customer never selects a technician or a bay. They choose *what*, *when* and *where*; the
server chooses *who* and *which*.

**1 — Select vehicle.** From the customer's garage. Fixes `vehicleId` (FR 1).

**2 — Select service type.** Fixes the appointment's `durationMinutes`.

**3 — Select a date.** A single day.

**4 — Read availability.** `GET /availability?serviceTypeId=&date=` returns each dealership with
its bookable start times (6.1). The customer sees places and times only. **Advisory** — this
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
that actually upholds NFR 4.1. Only the second is trusted.
