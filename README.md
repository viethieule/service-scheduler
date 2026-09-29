# Unified Service Scheduler

Appointment scheduler for automotive dealership servicing (Keyloop Scenario A, backend).
The design lives in [docs/DESIGN.md](docs/DESIGN.md); this README covers running it and how
it was built with AI.

The implementation targets the hardest part of the problem: **never double-booking a service
bay or a technician under concurrency**. Availability is advisory; the booking confirm is
settled by PostgreSQL exclusion constraints.

## Requirements

- .NET 10 SDK
- PostgreSQL 17
- PowerShell 7 (`pwsh`) — only for `scripts/burst.ps1`

## Configuration

No credential is committed. Supply your own.

**API** — user secrets:

```bash
dotnet user-secrets set "ConnectionStrings:Scheduler" \
  "Host=localhost;Port=5432;Database=servicescheduler;Username=postgres;Password=<yours>" \
  --project src/ServiceScheduler.API
```

**Tests** — environment variable, without a database name (each run creates and drops its own):

```bash
export SCHEDULER_TEST_DB="Host=localhost;Port=5432;Username=postgres;Password=<yours>"
```

## Running

```bash
dotnet run --project src/ServiceScheduler.API
```

In Development, migrations and seed data are applied on startup. Swagger UI:
<http://localhost:5080/swagger>.

Seed data: dealership `1` with two bays and two technicians, customer `1` owning vehicle `1`,
and service types `1` Oil change (60 min), `2` Tyre change (60), `3` Diagnostic (90),
`4` Brake service (120). All times are UTC; the business day is 08:00–18:00.

## Endpoints

```
GET  /availability?serviceTypeId={int}&date={yyyy-MM-dd}
POST /bookings
```

`201` on confirmation, `409` when no bay or technician is free for the whole duration,
`503` when the dealership-day lock could not be acquired in time, `422` for invalid requests.

### curl examples

```bash
DATE=2027-03-01   # any future date

# Bookable start times for a brake service
curl "http://localhost:5080/availability?serviceTypeId=4&date=$DATE"

# Book 09:00 at dealership 1 for the seeded vehicle
curl -i -X POST http://localhost:5080/bookings \
  -H "Content-Type: application/json" \
  -d "{\"dealershipId\":1,\"vehicleId\":1,\"serviceTypeId\":4,\"startAt\":\"${DATE}T09:00:00Z\"}"
```

Run the `POST` three times. The first two return `201` on different bays and technicians; the
third returns `409`, and the availability call no longer offers 08:00 to 10:30.

```bash
# Someone else's vehicle: 422
curl -i -X POST http://localhost:5080/bookings \
  -H "Content-Type: application/json" -H "X-Customer-Id: 2" \
  -d "{\"dealershipId\":1,\"vehicleId\":1,\"serviceTypeId\":1,\"startAt\":\"${DATE}T14:00:00Z\"}"
```

## Testing

```bash
dotnet test
```

Ten integration tests against a real PostgreSQL database, covering the slot grid, how a
booking removes only the slots it overlaps, back-to-back appointments, validation, and
concurrent bookings. They are not mocked: the invariant under test is a database constraint.
EF Core's InMemory provider enforces no constraints, and SQLite has neither `gist` nor
`EXCLUDE`, so mocking would make the most important test pass without proving anything.

### Contention burst

With the API running:

```powershell
./scripts/burst.ps1 -Count 20
```

Two bays and two technicians are seeded, so a burst of concurrent bookings at one start time
produces **exactly two** confirmations and the rest `409`. This is deterministic, not a race
won by luck.

Use a different `-StartAt` for each run. A second run against the same slot finds it already
full and reports `0 x 201` — the slot is used, not the lock broken. The start must be in the
future and the 120-minute service must end by 18:00, so 16:00 is the latest valid start.

```powershell
1..5 | ForEach-Object {
  $d = (Get-Date).ToUniversalTime().Date.AddDays(300 + $_).AddHours(9).ToString('yyyy-MM-ddTHH:mm:ssZ')
  ./scripts/burst.ps1 -Count 25 -StartAt $d
}
```

Check the invariant directly — this must return zero:

```sql
SELECT count(*) FROM appointments a
JOIN appointments b
  ON a.id <> b.id
 AND (a.technician_id = b.technician_id OR a.service_bay_id = b.service_bay_id)
 AND tstzrange(a.start_at, a.end_at, '[)') && tstzrange(b.start_at, b.end_at, '[)')
WHERE a.status = 1 AND b.status = 1;
```

## How double-booking is prevented

### The floor: exclusion constraints

Correctness does not depend on application code. Two PostgreSQL exclusion constraints are
created in the initial migration:

```sql
ALTER TABLE appointments
  ADD CONSTRAINT appointments_no_overlap_technician
  EXCLUDE USING gist (
    technician_id WITH =,
    tstzrange(start_at, end_at, '[)') WITH &&
  ) WHERE (status = 1);
```

There is a second, identical constraint on `service_bay_id`. They are separate on purpose: one
combined constraint would only reject rows matching on *both* resources, which would let the
same technician be booked twice in different bays. `'[)'` lets back-to-back appointments touch
without colliding, and the partial `WHERE` means a cancelled appointment frees its slot.

### Above it: a dealership-day advisory lock

The constraints keep the data correct, but on their own they reject the loser of a race even
when another bay is free, so capacity goes unused. `CreateBookingAsync` therefore serialises
bookings per dealership per date:

```sql
SET LOCAL lock_timeout = '2s';
SELECT pg_advisory_xact_lock(:dealership_id, :day_number);
```

The lock is held until commit and released automatically. Under it, the availability re-read
sees committed state, so the bay and technician are chosen from what is actually free.

- **A pre-check read runs outside the lock.** It never chooses anything; it only decides
  whether to queue at all. When a dealership is full, callers get `409` without waiting behind
  the lock.
- **`lock_timeout` bounds the wait.** Exceeding it yields `503`, not `409` — capacity was never
  determined, so "no capacity" would be false and would hide load.
- **Bookings must fit inside the business day.** That keeps every window inside one date, so
  one lock is enough. A window crossing midnight would need two locks in a strict order to
  avoid deadlock.

The constraints remain mandatory: the lock protects only callers that take it, while the
constraints also police migrations, admin tools and anything else writing to `appointments`.

## Layout

| Project | Role |
| --- | --- |
| `ServiceScheduler.Shared` | `IServiceContext`, `Result<T>`, `BookingError`. No dependencies. |
| `ServiceScheduler.Data` | Entities, `SchedulerDbContext`, migrations, seed. |
| `ServiceScheduler.Services` | `BookingService`. Uses EF Core directly — no repository, by design. Never references ASP.NET. |
| `ServiceScheduler.API` | Minimal API endpoints, DI, OpenAPI. |
| `ServiceScheduler.Tests` | Integration tests against a real PostgreSQL database. |

## Not yet implemented

Authentication, technician qualification, `GET /bookings/{id}` (the `201` `Location` header
already points at it), cancellation, opening hours, timezones, idempotency, admission control,
metrics and tracing. Logging is in place.

## AI Collaboration Narrative

I built this with Claude. The model sped up drafting code and documentation and supplied PostgreSQL knowledge
I did not already have to hand. I set the architecture, made the structural decisions, and
checked what it produced against a running system. The design-phase account is in
[docs/DESIGN.md §10](docs/DESIGN.md#10-use-of-generative-ai).

### Strategy: I set the shape, the model fills it in

- **I specified the architecture rather than asking for one:** four projects, EF Core as the
  unit of work with no repository, `IServiceContext` in Shared, no authentication yet. The
  model reviewed it and added two useful points: EF Core cannot express `EXCLUDE`, so it needs
  hand-written SQL in the migration, and seeding two bays and two technicians turns the burst
  result into a checkable number.
- **I cut scope aggressively.** The model's default is a richer domain than the requirements
  need. Operating hours, shifts, certifications, `is_active` flags and timezone columns all went
  out. Its first observability proposal was full OpenTelemetry: eight metrics, four spans and
  tail sampling. I asked what the essentials were, then what problem each log actually solves,
  and implemented only the two log events I could justify.
- **I questioned the design before any code was written.** For concurrency I asked whether
  retry was needed, whether it could loop forever, whether a hot dealership becomes a
  bottleneck, why the lock is not keyed on `(bay, technician, day)`, and whether this was simply
  pessimistic locking. Implementation started only after those answers held up.
- **I challenged defaults.** I asked why Guid was the primary key and whether it hurts the
  index, and switched to int identity keys.

### What the model got right, and where it was wrong

It contributed the key technique: `EXCLUDE` constraints, the insight that there must be **two**
of them, the half-open range, and the partial index for cancellation. It also proposed the
pre-check read once I raised the hot-dealership problem, and it caught that `POST /bookings`
accepted start times crossing midnight, which would break the one-lock-per-transaction design.

Questioning exposed its mistakes:

- **The wrong lock key.** Its first advisory-lock key was `(dealership, slot)`. That fails for
  multi-slot services: a 120-minute booking at 09:00 and another at 10:00 overlap but take
  different locks. I had it corrected to `(dealership, date)`.
- **Misleading comments.** They said one read was "without a lock", as if the other took one.
  Both are plain MVCC reads; the difference is whether the advisory lock is held. The comments
  now say *inside* and *outside the critical section*.
- **An overclaim about the pre-check.** It first said the pre-check protects the connection
  pool. Pressed with "can 100 requests still queue?", it narrowed the claim: the pre-check only
  filters requests that were already doomed on arrival, so a burst against an *empty*
  dealership-day still queues in full. What actually bounds that is admission control, which is
  not built. The resulting estimate of the pool limit is in
  [DESIGN.md §9](docs/DESIGN.md#9-building-for-the-future).

### Verification: nothing is true until the system says so

- **Real database, no mocks.** Mocking the database would have made the key test pass without
  proving anything.
- **A wrong test assertion.** The model's burst test asserted *exactly* two confirmations before
  anything made that guarantee: without the lock, two callers could collide on one pair while
  another was free. I weakened it to "never more than capacity" and restored strict equality
  only once the lock made the outcome deterministic.
- **A test that passed for the wrong reason.** I checked the lock timeout by holding the lock
  from a separate `psql` session: `503` after 2.08 s, with the expected warning. The first
  attempt returned `201`. The day number had been computed in SQL, and PostgreSQL uses the
  Julian calendar before 1582 while .NET uses the proleptic Gregorian, so it locked the wrong
  day. It was caught because `201` was the wrong answer, not because anything errored.
- **Repeated runs, not one lucky one.** Five burst runs, `pg_constraint` queries confirming the
  constraints exist as described, and the overlap query above returning zero.
- **A leaked secret.** The model wrote my database password into two tracked files, then
  flagged it before the first commit. I moved it to user secrets and an environment variable.
  Reviewing generated changes has to cover what they leak, not only what they do.

**Still unverified, stated plainly.** The overlap-rejection path (`23P01`) is unreachable
through the service by construction, because the lock prevents it. It guards against writers
that bypass the service. The pre-check is not exercised by `burst.ps1`, because in a
simultaneous burst every caller reads before anyone commits.
