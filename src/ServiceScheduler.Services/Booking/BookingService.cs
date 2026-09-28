using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceScheduler.Data;
using ServiceScheduler.Data.Entities;
using ServiceScheduler.Shared;

namespace ServiceScheduler.Services.Booking;

public class BookingService(
    SchedulerDbContext db,
    IServiceContext serviceContext,
    ILogger<BookingService> logger) : IBookingService
{
    /// <summary>PostgreSQL <c>exclusion_violation</c>: an overlap constraint rejected the row.</summary>
    private const string ExclusionViolation = "23P01";

    /// <summary>PostgreSQL <c>lock_not_available</c>: <c>lock_timeout</c> elapsed while waiting.</summary>
    private const string LockNotAvailable = "55P03";

    public async Task<Result<AvailabilityResponse>> GetAvailabilityAsync(
        int serviceTypeId,
        DateOnly date,
        CancellationToken ct = default)
    {
        var serviceType = await db.ServiceTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceTypeId, ct);

        if (serviceType is null)
            return Result<AvailabilityResponse>.Fail(BookingError.ServiceTypeNotFound);

        var duration = TimeSpan.FromMinutes(serviceType.DurationMinutes);
        var (dayStart, dayEnd) = BusinessDay(date);

        var dealerships = await db.Dealerships
            .AsNoTracking()
            .Include(d => d.ServiceBays)
            .Include(d => d.Technicians)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        // Every confirmed appointment touching the business day, in one query.
        var booked = await db.Appointments
            .AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Confirmed
                        && a.StartAt < dayEnd
                        && a.EndAt > dayStart)
            .Select(a => new BusyInterval(a.ServiceBayId, a.TechnicianId, a.StartAt, a.EndAt))
            .ToListAsync(ct);

        var results = new List<DealershipAvailability>();

        foreach (var dealership in dealerships)
        {
            var slots = new List<string>();

            for (var start = dayStart; start + duration <= dayEnd;
                 start = start.AddMinutes(SchedulingConstants.SlotGranularityMinutes))
            {
                var end = start + duration;

                // The two checks are independent: one free bay and one free technician,
                // not a matched pair. Pairing happens only at booking.
                var bayFree = dealership.ServiceBays.Any(b =>
                    !booked.Any(x => x.ServiceBayId == b.Id && x.Overlaps(start, end)));

                if (!bayFree) continue;

                var technicianFree = dealership.Technicians.Any(t =>
                    !booked.Any(x => x.TechnicianId == t.Id && x.Overlaps(start, end)));

                if (!technicianFree) continue;

                slots.Add(TimeOnly.FromDateTime(start).ToString("HH:mm"));
            }

            if (slots.Count > 0)
                results.Add(new DealershipAvailability(dealership.Id, dealership.Name, slots));
        }

        return Result<AvailabilityResponse>.Ok(new AvailabilityResponse(
            serviceTypeId,
            serviceType.DurationMinutes,
            date,
            SchedulingConstants.SlotGranularityMinutes,
            results));
    }

    public async Task<Result<BookingConfirmation>> CreateBookingAsync(
        CreateBookingCommand command,
        CancellationToken ct = default)
    {
        var serviceType = await db.ServiceTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == command.ServiceTypeId, ct);

        if (serviceType is null)
            return Result<BookingConfirmation>.Fail(BookingError.ServiceTypeNotFound);

        var startAt = ToUtc(command.StartAt);
        var endAt = startAt.AddMinutes(serviceType.DurationMinutes);

        if (startAt < DateTime.UtcNow)
            return Result<BookingConfirmation>.Fail(BookingError.DateInPast);

        // Also keeps every window inside one calendar day, which is what lets the booking
        // take a single dealership-day lock. A window crossing midnight would need two
        // locks and a strict ordering between them to stay deadlock-free.
        if (!WithinBusinessDay(startAt, endAt))
            return Result<BookingConfirmation>.Fail(BookingError.OutsideBusinessHours);

        var vehicle = await db.Vehicles
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == command.VehicleId, ct);

        if (vehicle is null)
            return Result<BookingConfirmation>.Fail(BookingError.VehicleNotFound);

        if (vehicle.CustomerId != serviceContext.CustomerId)
            return Result<BookingConfirmation>.Fail(BookingError.VehicleNotOwned);

        var dealership = await db.Dealerships
            .AsNoTracking()
            .Include(d => d.ServiceBays)
            .Include(d => d.Technicians)
            .FirstOrDefaultAsync(d => d.Id == command.DealershipId, ct);

        if (dealership is null)
            return Result<BookingConfirmation>.Fail(BookingError.DealershipNotFound);

        // Read #1 - unlocked, and never used to choose anything. Its only job is to keep
        // doomed requests out of the lock queue: when a dealership is already full, the
        // callers who cannot win return here instead of serialising behind the lock just
        // to be told the same thing.
        //
        // The two staleness directions are not symmetric. A stale "free" costs one wasted
        // lock acquisition, because read #2 then tells the truth. A stale "full" returns
        // NoCapacity while a slot exists, which needs a cancellation to land inside this
        // microsecond window; the caller sees the slot on their next availability query.
        var precheck = await LoadBusyAsync(dealership.Id, startAt, endAt, ct);

        if (!HasCapacity(dealership, precheck))
            return Result<BookingConfirmation>.Fail(BookingError.NoCapacity);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var lockWait = Stopwatch.StartNew();

        try
        {
            await AcquireDayLockAsync(dealership.Id, startAt, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            // Load, not a capacity answer. Nothing about this attempt reaches the database,
            // so this line is the only record that it happened.
            logger.LogWarning(
                "Booking lock timed out for dealership {DealershipId} on {BookingDate} "
                + "after {LockWaitMs}ms. The dealership-day schedule is contended.",
                dealership.Id,
                DateOnly.FromDateTime(startAt),
                lockWait.ElapsedMilliseconds);

            return Result<BookingConfirmation>.Fail(BookingError.Busy);
        }

        // Read #2 - under the lock, and the only read the booking decision rests on.
        var booked = await LoadBusyAsync(dealership.Id, startAt, endAt, ct);

        var bay = dealership.ServiceBays
            .FirstOrDefault(b => !booked.Any(x => x.ServiceBayId == b.Id));

        var technician = dealership.Technicians
            .FirstOrDefault(t => !booked.Any(x => x.TechnicianId == t.Id));

        if (bay is null || technician is null)
            return Result<BookingConfirmation>.Fail(BookingError.NoCapacity);

        var appointment = new Appointment
        {
            DealershipId = dealership.Id,
            CustomerId = serviceContext.CustomerId,
            VehicleId = vehicle.Id,
            ServiceTypeId = serviceType.Id,
            TechnicianId = technician.Id,
            ServiceBayId = bay.Id,
            StartAt = startAt,
            EndAt = endAt,
            Status = AppointmentStatus.Confirmed,
            CreatedAt = DateTime.UtcNow
        };

        db.Appointments.Add(appointment);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsOverlapRejection(ex, out var violation))
        {
            // Should be unreachable: the lock above serialises every caller that goes
            // through this service. Reaching it means either something wrote to
            // appointments without taking the lock, or the lock itself regressed - which
            // is otherwise invisible, because bookings keep succeeding either way.
            logger.LogWarning(
                "Overlap constraint {ConstraintName} rejected a booking that held the "
                + "dealership-day lock. Dealership {DealershipId}, {StartAt:o} to {EndAt:o}, "
                + "bay {ServiceBayId}, technician {TechnicianId}. Either a writer bypassed "
                + "the lock or the locking path has regressed.",
                violation.ConstraintName,
                dealership.Id,
                startAt,
                endAt,
                bay.Id,
                technician.Id);

            db.Entry(appointment).State = EntityState.Detached;
            return Result<BookingConfirmation>.Fail(BookingError.NoCapacity);
        }

        await transaction.CommitAsync(ct);   // releases the advisory lock

        return Result<BookingConfirmation>.Ok(new BookingConfirmation(
            appointment.Id,
            appointment.DealershipId,
            appointment.VehicleId,
            appointment.ServiceTypeId,
            appointment.TechnicianId,
            appointment.ServiceBayId,
            appointment.StartAt,
            appointment.EndAt));
    }

    /// <summary>
    /// Serialises bookings for one dealership on one date. The two-integer form of
    /// <c>pg_advisory_xact_lock</c> avoids hashing a composite key, so distinct
    /// dealership-days can never collide onto one lock.
    ///
    /// The <c>xact</c> variant releases on commit or rollback. The session-scoped variant
    /// would outlive the transaction and return to the connection pool still held.
    /// </summary>
    private async Task AcquireDayLockAsync(int dealershipId, DateTime startAt, CancellationToken ct)
    {
        var dayNumber = DateOnly.FromDateTime(startAt).DayNumber;

        // SET LOCAL reverts at the end of this transaction, so it cannot leak to whoever
        // borrows this pooled connection next.
        await db.Database.ExecuteSqlRawAsync(
            $"SET LOCAL lock_timeout = '{SchedulingConstants.BookingLockTimeout}'; "
            + "SELECT pg_advisory_xact_lock({0}, {1});",
            [dealershipId, dayNumber],
            ct);
    }

    private Task<List<BusyInterval>> LoadBusyAsync(
        int dealershipId, DateTime startAt, DateTime endAt, CancellationToken ct) =>
        db.Appointments
            .AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Confirmed
                        && a.DealershipId == dealershipId
                        && a.StartAt < endAt
                        && a.EndAt > startAt)
            .Select(a => new BusyInterval(a.ServiceBayId, a.TechnicianId, a.StartAt, a.EndAt))
            .ToListAsync(ct);

    private static bool HasCapacity(Dealership dealership, List<BusyInterval> booked) =>
        dealership.ServiceBays.Any(b => !booked.Any(x => x.ServiceBayId == b.Id))
        && dealership.Technicians.Any(t => !booked.Any(x => x.TechnicianId == t.Id));

    private static bool WithinBusinessDay(DateTime startAt, DateTime endAt) =>
        DateOnly.FromDateTime(startAt) == DateOnly.FromDateTime(endAt.AddTicks(-1))
        && TimeOnly.FromDateTime(startAt) >= SchedulingConstants.BusinessDayStart
        && TimeOnly.FromDateTime(endAt.AddTicks(-1)) < SchedulingConstants.BusinessDayEnd;

    private static bool IsOverlapRejection(DbUpdateException ex, out PostgresException violation)
    {
        if (ex.InnerException is PostgresException { SqlState: ExclusionViolation } postgres)
        {
            violation = postgres;
            return true;
        }

        violation = null!;
        return false;
    }

    private static (DateTime Start, DateTime End) BusinessDay(DateOnly date)
    {
        var start = DateTime.SpecifyKind(
            date.ToDateTime(SchedulingConstants.BusinessDayStart), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(
            date.ToDateTime(SchedulingConstants.BusinessDayEnd), DateTimeKind.Utc);
        return (start, end);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private readonly record struct BusyInterval(
        int ServiceBayId,
        int TechnicianId,
        DateTime StartAt,
        DateTime EndAt)
    {
        /// <summary>Half-open comparison, matching <c>tstzrange(..., '[)')</c> in the database.</summary>
        public bool Overlaps(DateTime start, DateTime end) => StartAt < end && EndAt > start;
    }
}
