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

Pass a different `-StartAt` on each run. The default slot is the same all day, so a second
run against it finds the capacity already taken and reports `0 x 201` — the slot is used, not
the lock broken. The start must be in the future and the service must finish by 18:00 UTC, so
16:00 is the latest valid start for the 120-minute brake service the script books by default.

```powershell
1..5 | ForEach-Object {
  $d = (Get-Date).ToUniversalTime().Date.AddDays(300 + $_).AddHours(9).ToString('yyyy-MM-ddTHH:mm:ssZ')
  ./scripts/burst.ps1 -Count 25 -StartAt $d
}
```

Two mechanisms make the result deterministic, at different levels.

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

- **A pre-check read runs outside the critical section.** It never chooses anything; it only
  decides whether to join the lock queue at all. When a dealership is full, the callers who
  cannot win return `409` without serialising behind the lock. That stops a hot dealership
  queueing thousands of doomed requests and exhausting the connection pool. Both reads are
  ordinary MVCC reads taking no row locks — what separates them is whether the advisory lock
  is held, not how they read. The second is trustworthy because nothing else can commit
  between it and the insert.
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
cancellation, opening hours, technician certification, timezones, idempotency, metrics and
tracing (logging is in place).

## AI Collaboration Narrative

This is a living section. I am updating it as the work continues, so it describes where the
project actually stands rather than how I intended to work at the start. The design-phase
counterpart is *Use of Generative AI* in [docs/DESIGN.md](docs/DESIGN.md).

I built this with Claude. It accelerated drafting and supplied specific PostgreSQL knowledge I
did not already have to hand; I directed the work, made the structural decisions and verified
its output against a running system. The git history is short enough to read in full, and it
should corroborate that account rather than contradict it.

### Strategy: decide the shape myself, then delegate

My working rule was that the model drafts and I decide. Before accepting any design I
interrogated the domain assumptions underneath it — who owns dealership resource data, and who
owns the vehicle — and only picked dealership-as-tenant and customer-owned vehicles after
reasoning through the alternatives. The architecture was specified rather than proposed: four
projects, clean architecture with an explicit exception for persistence, EF Core as the unit of
work, no repository, `IServiceContext` in Shared, no authentication yet.

The other half of the strategy was cutting scope. The model's default is a richer model than
the requirements need, and the design shrank under pressure at every step:
`DealershipOperatingHours` and `TechnicianShift` removed, timezone and DST handling pushed out
of scope, `TechnicianSkill` removed, `is_active` flags removed, the `Dealership` timezone column
removed. `ServiceType` went down to an enum and came back as a small entity only when duration
gave it a reason to exist. At one point I edited `docs/DESIGN.md` by hand to simplify it and then
asked the model to re-read it and reconcile the cross-references it had left dangling.

I also pushed back on individual answers rather than accepting the first one. I asked why Guid v7
was the default and whether it actually benefits the index, and chose int identity keys. I noticed
that the Resource Service had become redundant once availability returned dealership data
inline — the model had not raised it. I challenged its claim about where the availability
endpoint belonged, and asked directly whether REST compliance held for `/availability`, which
surfaced a real gap: `201 Created` with no `Location` header and no `GET /bookings/{id}`. I
deferred that gap knowingly rather than take the fix on the spot, and it is listed above as
outstanding.

The concurrency design took the most iteration. I did not accept the first proposal. I asked
whether retry was needed, whether retry could loop forever, whether the advisory lock was there to
prevent that loop — it was not, and the model's framing of it was ambiguous until I said so —
whether a hot dealership becomes a bottleneck, why the lock is not taken on
`(bay, technician, day)`, and whether what we were describing was simply pessimistic locking.
Implementation started only after those exchanges.

The same pattern ran through observability. The first proposal was a full OpenTelemetry design:
eight metrics, four spans, tail sampling, Postgres lock dashboards. I asked what the *key* things
were, and cut it to three metrics, one span and two log events. Then, because I am not fluent in
tracing yet, I asked what problems those logs actually solve before agreeing to any of it — and
only implemented the logs, leaving metrics and tracing for later.

### Where the model contributed materially

It proposed the `EXCLUDE` constraint approach, including the detail that two separate constraints
are required — one combined constraint only rejects rows matching on both bay and technician,
which would leave a technician bookable twice in different bays. It proposed the half-open `'[)'`
range so back-to-back appointments do not falsely collide, and the partial `WHERE (status = 1)` so
a cancellation frees its slot. It proposed the pre-check read in front of the advisory lock,
after I raised the hot-dealership bottleneck. And it caught that `POST /bookings` accepted start
times outside the business day, which would cross midnight and break the
single-lock-per-transaction assumption. Those are real contributions and I am not going to
understate them.

It also corrected itself twice under questioning, which is worth recording because it is the
reason the questioning was worth doing. Its first advisory-lock key was `(dealership, slot)`,
which does not work for multi-slot durations: a 120-minute booking at 09:00 and another at 10:00
overlap but hash to different keys. The key became `(dealership, date)`. And when I asked whether
reading without a lock was not simply the default, it conceded the comments were misleading —
both reads are ordinary MVCC reads taking no row locks, and what separates them is whether the
advisory lock is held. The comments now say that.

### Verification: nothing is true until the system says so

I treated model output as a proposal to be tested, not as a result. Every claim about behaviour
was checked against a running system: builds, the ten integration tests, five repeated burst runs
rather than one lucky one, direct `psql` queries against `pg_constraint` to confirm the constraints
exist as described, and the overlap-detection query at the end of *Verifying the invariant*, which
must return zero rows.

The tests run against a real PostgreSQL database rather than mocks or EF InMemory. That is a
deliberate constraint on the model as much as on me: the invariant under test is a database
constraint, so mocking the database away would have made the most important test in the suite
vacuous while still passing.

That discipline found a genuine defect in the model's own test assertions. `scripts/burst.ps1` and
the contention test asserted that a concurrent burst confirms *exactly* the seeded capacity —
which the implementation did not guarantee at that point, since assignment picked one candidate
pair without retrying, so callers could collide on the same pair while another was free. I
corrected the assertions to the property that actually held, never more than capacity, and
restored strict equality only after the advisory lock made the outcome deterministic. A passing
test that asserts the wrong thing is worse than no test, and that is the clearest evidence I can
offer that the output here was verified empirically rather than trusted.

The lock-timeout log was verified the same way, by holding the advisory lock from a separate
`psql` session and booking against it: `503` after 2.08 seconds, with the warning line carrying
the real dealership and wait time. The first attempt at that test passed *wrongly* — the day
number was computed in SQL, but PostgreSQL uses the Julian calendar before 1582 while .NET uses
the proleptic Gregorian, so it locked the adjacent day and the booking succeeded. It was caught
because `201` was the wrong answer, not because anything errored.

Two paths remain unverified at runtime and I would rather say so than imply otherwise. The
`23P01` conflict log is unreachable through the service by construction, since the lock prevents
it; it guards against writers that bypass the service. And `burst.ps1` does not exercise the
pre-check at all: every caller runs its first read before anyone commits, so nobody is filtered.
The pre-check is a saturation optimisation, and the burst models a contention spike.

One more, of a different kind, and this one goes the other way: the model had written my
PostgreSQL password into two tracked files, then flagged before the first commit that pushing
them would put it in git history permanently. I chose to move it to user secrets and an
environment variable. Worth recording honestly — the tool created the exposure and also caught
it, and the decision about how to handle it was mine. Review of generated changes has to cover
what they leak, not only what they do.

### Implementation-phase question log

A condensed record from planning the architecture onward.

| I asked | Outcome |
| --- | --- |
| Review my proposed four-project architecture | Sound. Flagged that EF Core cannot express an `EXCLUDE` constraint, so it needs hand-written SQL in the migration, and that seeding two bays and two technicians makes the burst result a number rather than "one wins" |
| Do I need a repository abstraction to unit test? | No. Mocking the database would make the invariant test vacuous. Two tiers instead: pure functions without a database, integration tests with a real one |
| Why Guid for the primary key instead of int? | Non-enumerable ids and no sequence coordination, at the cost of key width. Judgement call, not a clear win |
| Does Guid not hurt the index? | v7 fixes insert locality; the width penalty remains. Chose int identity |
| Swagger reports a validation error and sends no request | .NET 10 types an int query parameter as an "integer or string" union that Swagger UI cannot render. Fixed with a schema transformer |
| Two users race; one gets 409. Do we need retry, and can it loop forever? | Retry is needed to use capacity; bounded by `bays + technicians` if each attempt excludes a resource |
| Is the advisory lock there to prevent that loop? | No — they are alternatives. The lock stops the race happening, so there is nothing to retry |
| At high scale, does the lock degrade performance? | Granularity decides it. The proposed `(dealership, slot)` key was wrong for multi-slot durations; corrected to `(dealership, date)` |
| Do we need an application or distributed lock? | No. The database advisory lock already is one, and it commits atomically with the write, which an external lock cannot |
| Can one hot dealership become a bottleneck? | Yes — the lock serialises attempts, not just successes. Led to the pre-check read in front of the lock |
| Why not lock on `(bay, technician, day)`? | You cannot lock what you have not chosen, and one composite key constrains neither resource — the same mistake as a single combined `EXCLUDE` |
| Is this pessimistic locking, and why read without the lock? | Pessimistic lock over an optimistic floor. Two reads: one outside the critical section as a filter, one inside as the decision |
| Propose logging, metrics and tracing | Full OpenTelemetry proposal |
| That is a lot — what are the key things? | Three metrics, one span, two log events |
| What problems do those logs actually solve? | The regression that is otherwise invisible: remove the lock and bookings still succeed, but the conflict counter goes from zero |
| Is reading without a lock not the default? | Yes. The comments were misleading and were reworded to say *inside* and *outside the critical section* |
| What happens if read #2 is dropped and read #1 used? | The lock becomes decoration: cost of pessimism, outcome of optimism |
| Can the results of the two reads differ? Show an example | Three timelines, including the one where both say "capacity exists" but name different resources |
| Does `burst.ps1` test that scenario? | Two of three. Not the pre-check, because a simultaneous burst filters nobody |
