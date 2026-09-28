using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceScheduler.Data;
using ServiceScheduler.Data.Entities;
using ServiceScheduler.Shared;

namespace ServiceScheduler.Services.Booking;

public class BookingService(SchedulerDbContext db, IServiceContext serviceContext) : IBookingService
{
    /// <summary>PostgreSQL <c>exclusion_violation</c>: an overlap constraint rejected the row.</summary>
    private const string ExclusionViolation = "23P01";

    public async Task<Result<AvailabilityResponse>> GetAvailabilityAsync(
        Guid serviceTypeId,
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

        // Read-then-insert with no lock. Two concurrent callers can both pass this
        // check; the exclusion constraints in the database settle it, and the loser
        // surfaces as NoCapacity below.
        var booked = await db.Appointments
            .AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Confirmed
                        && a.DealershipId == dealership.Id
                        && a.StartAt < endAt
                        && a.EndAt > startAt)
            .Select(a => new BusyInterval(a.ServiceBayId, a.TechnicianId, a.StartAt, a.EndAt))
            .ToListAsync(ct);

        var bay = dealership.ServiceBays
            .FirstOrDefault(b => !booked.Any(x => x.ServiceBayId == b.Id));

        var technician = dealership.Technicians
            .FirstOrDefault(t => !booked.Any(x => x.TechnicianId == t.Id));

        if (bay is null || technician is null)
            return Result<BookingConfirmation>.Fail(BookingError.NoCapacity);

        var appointment = new Appointment
        {
            Id = Guid.CreateVersion7(),
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
        catch (DbUpdateException ex) when (IsOverlapRejection(ex))
        {
            db.Entry(appointment).State = EntityState.Detached;
            return Result<BookingConfirmation>.Fail(BookingError.NoCapacity);
        }

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

    private static bool IsOverlapRejection(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: ExclusionViolation };

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
        Guid ServiceBayId,
        Guid TechnicianId,
        DateTime StartAt,
        DateTime EndAt)
    {
        /// <summary>Half-open comparison, matching <c>tstzrange(..., '[)')</c> in the database.</summary>
        public bool Overlaps(DateTime start, DateTime end) => StartAt < end && EndAt > start;
    }
}
