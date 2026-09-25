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

### 4.2 Time correctness

Scheduling is expressed in dealership-local time, stored as UTC plus the dealership's IANA
timezone. Duration arithmetic runs on instants, so daylight-saving transitions cannot
create phantom availability or silent overlaps.

### 4.3 Read scalability and performance

The workload is read-skewed — customers browse many dealerships and dates before booking
once — and each availability query is an expensive interval search. Because 4.1 makes
these reads advisory they can be cached or served from replicas, and the workload
partitions cleanly by dealership for near-linear horizontal scale.

### 4.4 Observability

Structured logs with a correlation id across the booking flow, plus booking funnel metrics
(availability → attempt → confirmed / conflicted / rejected). Conflict rate is a
first-class signal: a rising rate means slot granularity or the concurrency strategy is
wrong.

### 4.5 Out of scope

Acknowledged but excluded to keep scope on booking: request idempotency, fail-closed
degradation, injectable clock for testability, audit trail, authorisation and tenant
security, durability of downstream effects, and data-driven scheduling policy.
