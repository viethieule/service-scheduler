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
GET  /availability?serviceTypeId={guid}&date={yyyy-MM-dd}
POST /bookings
```

`201` on confirmation, `409` when no bay or technician is free for the whole duration,
`422` for anything else.

## Verifying the invariant

```bash
dotnet test
./scripts/burst.ps1 -Count 20
```

Two service bays and two technicians are seeded, so a burst of concurrent bookings at one
start time must produce **exactly two** confirmations. Everything else must be `409`.

The guarantee is not in application code. It is two PostgreSQL exclusion constraints created
in the initial migration:

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

Check directly — this must return zero:

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

Authentication, retry and locking strategies above the constraint, `GET /bookings/{id}`,
cancellation, opening hours, technician certification, timezones, idempotency, metrics.
