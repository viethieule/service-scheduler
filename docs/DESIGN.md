# Unified Service Scheduler — System Design

## 1. Context

In the **Ownership** domain — the post-purchase relationship between a customer and their
vehicle — servicing is the main recurring touchpoint. Most dealerships still book it
manually: phone calls and diaries, with an advisor reconciling by hand which bay is free
and which technician is qualified. It is slow, error-prone, unavailable out of hours, and
double-books resources.

The **Unified Service Scheduler** replaces this with one application that lets a vehicle
owner book directly, guaranteeing every confirmed appointment has a service bay and a
technician reserved for its full duration — under one scheduling model shared across all
dealerships on the platform.

## 2. Assumptions

The brief leaves these open. Each is a deliberate call, not an accident of the code.

1. **Dealership is a tenant** of our system and owns its resource data (service bays,
   technicians, service types) inside it. Real dealership schedules live in a DMS whose
   interfaces are slow, often batch, and cannot atomically hold a slot, so a federated
   availability check on the booking path is not viable.
2. **Vehicles live inside our system**, owned by the customer and pre-registered. A booking
   references a `vehicleId`; VIN lookup and vehicle onboarding are out of scope.
3. **Every technician is qualified for every service type.** FR 2 asks for a *qualified*
   technician. Modelling qualification — a technician-to-service-type link — only narrows
   which technicians are candidates. It does not change how two bookings are prevented
   from overlapping, which is the hard problem here, so it is left out to keep the model
   small. Adding it later touches the candidate filter, not the constraints or the lock.
4. **Business day of 08:00–18:00 UTC** for every dealership, standing in for opening hours.
   Without some bound the slot grid runs midnight to midnight. It also keeps every booking
   inside one calendar date, which the locking design relies on (8.3).
5. **Offered start times sit on a 30-minute grid.** It is the knob trading precision against
   contention. `POST /bookings` does not enforce the grid; it only shapes what is offered.
6. **All times are UTC.** Contention is scoped to one dealership, so every compared timestamp
   shares a zone. Per-dealership local time is out of scope, but UTC storage is kept because
   it is expensive to retrofit and free now.
7. **The caller is identified by an `X-Customer-Id` header**, defaulting to the seeded
   customer. Authentication is out of scope, but the booking still needs an owner to check
   the vehicle against.

## 3. Technology Choices

**.NET and ASP.NET Core Web API.** This is the stack I know best, and I picked it so that the
effort in this exercise goes into the scheduling problem rather than into learning a framework.
Nothing in the design depends on it; the parts that carry the invariant live in the database.

**PostgreSQL.** This one is not a default. It was chosen for the `EXCLUDE` constraint over a
`btree_gist` index, which states the no-overlap invariant (NFR 5.1) once, declaratively, at
the data layer, instead of re-implementing it in every code path that writes an appointment.
The exclusion-constraint approach was Claude's proposal; choosing the database because of it
was my decision. The
hardest requirement drove the choice of store, rather than the store being picked first and
the requirement bent to fit it.

**Concurrency control: optimistic first, pessimistic only where it earns its place.** The
exclusion constraints came first and are always on: they are the floor, cost nothing until a
conflict occurs, and protect every writer. The dealership-day advisory lock was added
afterwards, on top, once the cost of optimism alone was clear — the loser of a race was
rejected even when a second bay was free. The lock exists to recover that wasted capacity,
not to establish correctness; remove it and utilisation degrades, but the data cannot be
corrupted.

**EF Core used directly in the service layer.** `BookingService` takes the `DbContext` as its
unit of work, with no repository in front. `DbSet<T>` already is a repository, and the two
things this design depends on most — the advisory lock and the exclusion-violation SQLSTATE —
are PostgreSQL specific and would leak through such an abstraction anyway. The service layer
is tied to EF Core, and I state that exception openly rather than pretend to portability.

## 4. Functional Requirements

1. **Resource Constrained Booking**: Allow a user to request a service appointment for a
   specific vehicle, service type, and dealership at a desired time.
2. **Real-Time Availability Check**: Before confirming, check for the availability of
   both a ServiceBay and a qualified Technician for the entire service duration.
   Qualification: see Assumption 3.
3. **Confirmed Appointment Record**: Upon success, create a persistent Appointment record
   associating the customer, vehicle, technician, and service bay.

## 5. Non-Functional Requirements

### 5.1 Consistency (highest priority)

No service bay or technician may ever be assigned to two overlapping appointments — a
hard invariant enforced at the data layer, not by application-level checks. Availability
reads are therefore **advisory** (a best-effort snapshot), while the booking confirm is
**authoritative and atomic**, failing a lost race with a clean conflict.

### 5.2 Observability

Kept deliberately small at this stage.

- **Implemented — two structured log events**, for the two booking outcomes that leave no
  other trace. A *lock timeout* (`503`) never reaches the database, so the log line is the
  only record it happened. An *overlap-constraint rejection* should be unreachable through
  the service; if it fires, a writer bypassed the lock or the locking path regressed —
  otherwise invisible, because bookings keep succeeding either way.
- **Planned — metrics**: a booking outcome counter (confirmed / no capacity / busy) and a
  lock-wait histogram. A rising conflict or busy rate is the signal that slot granularity or
  the concurrency strategy is wrong.
- **Planned — tracing**: one OpenTelemetry span around the booking transaction. Its W3C trace
  id is already exposed on `IServiceContext` for correlating logs, but is not yet attached to
  them.

### 5.3 Out of scope

Acknowledged but excluded to keep scope on preventing overlapping bookings: dealership
operating hours and technician roster calendars, technician qualification, timezone and
daylight-saving handling, request idempotency, authorisation and tenant security, audit
trail, and data-driven scheduling policy.

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
  resource is available whenever it is not already booked (Assumption 4 bounds the day).

## 7. API Endpoints

Both endpoints are owned by a single service (8.1).

```
GET  /availability?serviceTypeId=&date=
POST /bookings
```

### 7.1 `GET /availability`

Answers **"where and when can I bring the car in?"** — it returns candidate dealerships, each
with its bookable start times. Service bays and technicians are capacity that gates whether a
time is offered; they are never exposed to the customer and never chosen by them.

A start time on the 30-minute grid is offered when, for `window = [start, start + duration)`,
the dealership has at least one service bay free for the whole window **and** at least one
technician free for the whole window — checked independently, since a bay and a technician
are paired only at booking. Free means no overlapping appointment.

| Parameter | Required | Notes |
| --- | --- | --- |
| `serviceTypeId` | yes | determines the appointment duration |
| `date` | yes | a single day, `yyyy-MM-dd` |

```
GET /availability?serviceTypeId=4&date=2026-10-02
```

Response `200`:

```json
{
  "serviceTypeId": 4,
  "durationMinutes": 120,
  "date": "2026-10-02",
  "slotGranularityMinutes": 30,
  "dealerships": [
    { "dealershipId": 1, "name": "Northside Motors",
      "slots": ["08:00", "08:30", "13:00"] },
    { "dealershipId": 2, "name": "Riverside Auto",
      "slots": ["09:00", "15:30"] }
  ]
}
```

Dealerships are ordered by name; one with no free slots is omitted rather than returned empty.

| Status | Meaning |
| --- | --- |
| `200 OK` | possibly an empty `dealerships` array — a valid answer, not an error |
| `400 Bad Request` | missing or malformed parameter |
| `422 Unprocessable` | unknown `serviceTypeId` |

### 7.2 `POST /bookings`

```json
{
  "dealershipId": 1,
  "vehicleId": 1,
  "serviceTypeId": 4,
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
| `422 Unprocessable` | Unknown dealership, vehicle or service type; vehicle not owned by the caller; start in the past; window outside the business day |
| `503 Service Unavailable` | The dealership-day lock was not acquired in time — capacity was never determined, so retry |

The `Location` header points at `/bookings/{id}`, which is not implemented yet.

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
  cross-cutting concerns out of the service. Not implemented in this exercise.
- **Booking Service** — owns `Appointment`, and therefore availability computation and the
  write path. This is where the consistency invariant (NFR 5.1) is enforced.
- **Database** — one PostgreSQL instance, holding the exclusion constraints.

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
2. takes the dealership-day lock (8.3);
3. re-evaluates availability for `[start_at, end_at)` — the authoritative check;
4. picks one free bay and one free technician;
5. inserts the `Appointment`, with the database rejecting any overlap on either resource.

On success `201` with the assigned `technicianId` and `serviceBayId`. On a lost race `409`, and
the customer returns to step 4 with a refreshed slot list.

The dealership is an **output of step 4**, not an input. FR 1 still holds — `POST /bookings`
names a specific dealership — but the customer discovers it by availability rather than
choosing it up front.

Steps 4 and 7 both check availability, deliberately: step 4 is a fast, cacheable, advisory read
that makes the UI usable; step 7 is the transactional one that upholds NFR 5.1. Only the
second is trusted.

### 8.3 Concurrency

Two mechanisms, at different levels:

- **Exclusion constraints — the floor.** Two separate `EXCLUDE USING gist` constraints on
  `appointments`, one per resource, over `tstzrange(start_at, end_at, '[)')`, partial on
  confirmed status. One combined constraint would only reject rows matching on *both* bay and
  technician. They police every writer, including ones that bypass the service.
- **Dealership-day advisory lock — utilisation.** `pg_advisory_xact_lock(dealership_id, day)`
  serialises bookings for one dealership on one date, so the authoritative read sees committed
  state and a free bay is never wasted on a lost race. The lock is released on commit.
  `lock_timeout` bounds the wait and turns it into `503`.

A cheap pre-check read runs before the lock, so requests against an already-full dealership
return `409` without queueing.

## 9. Building for the Future

- **Scalability.** The workload is read-heavy: customers browse many dates before booking once.
  Because availability is advisory, it can be cached or served from a read replica without
  affecting correctness. Today it scans every dealership; at platform scale it needs a location
  filter and paging. Writes contend only within one dealership-day — the lock never makes two
  dealerships, or two days, wait on each other.
- **Performance.** Each booking holds its lock for a few milliseconds (one indexed read and one
  insert), so a single dealership-day can take hundreds of bookings per second — far above real
  demand for one site.
- **Reliability.** `lock_timeout` bounds how long a request waits; `503` (retry) is kept distinct
  from `409` (a real answer), so load is never reported as "no capacity". The known weak point
  is the **shared connection pool**: a simultaneous burst against one empty dealership-day
  queues on the lock while holding pooled connections. By estimate, ~1,000 simultaneous
  arrivals would delay the whole platform by about two seconds, and ~10,000 would exceed the
  pool timeout. The fixes are per-dealership admission control and separate pools for the read
  and write paths — not implemented.
- **Maintainability.** The invariant lives in the schema, so a new write path — an admin tool,
  a data migration — cannot break it by forgetting a check. Layers are small and one-way:
  API → Services → Data → Shared.
- **Observability.** See 5.2.

## 10. Use of Generative AI

I used Claude throughout the design phase as a **domain sounding board**, not a designer. The
pattern was the same each time: state my conjecture, ask what a real system in this domain
does, then decide. The implementation phase is covered in the README's *AI Collaboration
Narrative*.

**Filling the gaps.** The brief says nothing about where resource data lives, where vehicles
come from, how long a service takes, what "qualified" means, or when a dealership is open. The
assumptions in section 2 are the result of those exchanges — for example, I asked whether the
system should store bays and technicians or call the dealership's own APIs, and rejected
federation once it was clear a DMS cannot hold a slot.

**Cutting what the model added.** The model's instinct is a richer domain model than the
requirements need, and the design shrank at almost every step: operating hours, technician
shifts, technician certifications, `is_active` flags and per-dealership timezones all went out.
An early two-service split (a Resource Service beside the Booking Service) was folded into one.

**Where the ideas came from.**

- *Mine:* making the dealership an output of availability rather than an input; noticing that
  the Resource Service had nothing left to own once availability returned dealerships inline —
  the model had not raised it; keeping UTC storage when timezones went out of scope.
- *The model's, adopted:* never exposing bays and technicians to the customer, which would have
  promised a specific pair; the `EXCLUDE` constraint approach that decided the database.
- *Surfaced by asking, then deferred:* asking whether `/availability` was REST-compliant
  surfaced a `201` whose `Location` points at a `GET /bookings/{id}` that does not exist.
  I left it outstanding knowingly.

**Keeping the document honest.** I edited this document by hand to simplify it, then had the
model re-read it and fix the seven cross-references my edits had left dangling.
