# Unified Service Scheduler

Appointment scheduler for automotive dealership servicing. Design lives in
[docs/DESIGN.md](docs/DESIGN.md); this README covers running it.

The current stage exists to make the **booking contention problem** reachable and safe:
availability is advisory, and the confirm is settled by PostgreSQL exclusion constraints.

## Requirements

- .NET 10 SDK
- PostgreSQL 17
- `dotnet-ef` 10 (`dotnet tool update -g dotnet-ef`)

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
dotnet ef database update -p src/ServiceScheduler.Data -s src/ServiceScheduler.API
dotnet run --project src/ServiceScheduler.API
```

Swagger UI: <http://localhost:5080/swagger>. Migrations and seed data are applied
automatically in Development.

## Endpoints

```
GET  /availability?serviceTypeId={int}&date={yyyy-MM-dd}
POST /bookings
```

`201` on confirmation, `409` when no bay or technician is free for the whole duration,
`503` when the dealership-day lock could not be acquired in time, `422` for anything else.

## Verifying the invariant

```bash
dotnet test
./scripts/burst.ps1 -Count 20
```

Two service bays and two technicians are seeded, so a burst of concurrent bookings at one
start time produces **exactly two** confirmations. Everything else is `409`. This is
deterministic, not a race won by luck.

Two mechanisms make it so, at different levels.

### The floor: exclusion constraints

Correctness does not depend on application code. It is two PostgreSQL exclusion constraints
created in the initial migration:

```sql
ALTER TABLE appointments
  ADD CONSTRAINT appointments_no_overlap_technician
  EXCLUDE USING gist (
    technician_id WITH =,
    tstzrange(start_at, end_at, '[)') WITH &&
  ) WHERE (status = 1);
```

with a second, identical constraint on `service_bay_id`. They are separate on purpose: one
combined constraint would only reject rows matching on *both* resources, which would let the
same technician be booked twice in different bays.

### Above it: a dealership-day advisory lock

The constraints keep the data correct but reject the loser of a race, even when another bay
was free — so capacity went unused. `CreateBookingAsync` now serialises bookings per
dealership per date:

```sql
SET LOCAL lock_timeout = '2s';
SELECT pg_advisory_xact_lock(:dealership_id, :day_number);
```

Held to commit, released automatically. Under it the availability re-read sees committed
state, so the choice of bay and technician is made on the truth.

Three details carry the design:

- **An unlocked read runs first.** It never chooses anything; it only decides whether to join
  the lock queue at all. When a dealership is full, the callers who cannot win return `409`
  without serialising behind the lock. That stops a hot dealership queueing thousands of
  doomed requests and exhausting the connection pool.
- **`lock_timeout` bounds the wait.** Exceeding it yields `503`, not `409` — capacity was
  never determined, so reporting "no capacity" would be a lie that also hides load.
- **Bookings must fit inside the business day.** That keeps every window inside one calendar
  date, which is what lets a booking take a single lock. A window crossing midnight would
  need two, plus a strict ordering between them to stay deadlock-free.

The constraints remain mandatory: the lock protects only callers that take it, while the
constraints also police migrations, admin tools and anything else writing to `appointments`.

Check the invariant directly — this must return zero:

```sql
SELECT count(*) FROM appointments a
JOIN appointments b
  ON a.id <> b.id
 AND (a.technician_id = b.technician_id OR a.service_bay_id = b.service_bay_id)
 AND tstzrange(a.start_at, a.end_at, '[)') && tstzrange(b.start_at, b.end_at, '[)')
WHERE a.status = 1 AND b.status = 1;
```

## Layout

| Project | Role |
| --- | --- |
| `ServiceScheduler.Shared` | `IServiceContext`, `Result<T>`, `BookingError`. No dependencies. |
| `ServiceScheduler.Data` | Entities, `SchedulerDbContext`, migrations, seed. |
| `ServiceScheduler.Services` | `BookingService`. Depends on EF Core directly — no repository, by design. Never references ASP.NET. |
| `ServiceScheduler.API` | Minimal API endpoints, DI, OpenAPI. |
| `ServiceScheduler.Tests` | Integration tests against a real PostgreSQL database. |

Tests are not mocked. The invariant under test is a database constraint, so abstracting the
database away would prove nothing — EF Core's InMemory provider enforces no constraints, and
SQLite has neither `gist` nor `EXCLUDE`.

## Not yet implemented

Authentication, per-resource locking for hot dealerships, admission control, `GET /bookings/{id}`,
cancellation, opening hours, technician certification, timezones, idempotency, metrics.
