namespace ServiceScheduler.Services.Booking;

public sealed record CreateBookingCommand(
    Guid DealershipId,
    Guid VehicleId,
    Guid ServiceTypeId,
    DateTime StartAt);

public sealed record BookingConfirmation(
    Guid BookingId,
    Guid DealershipId,
    Guid VehicleId,
    Guid ServiceTypeId,
    Guid TechnicianId,
    Guid ServiceBayId,
    DateTime StartAt,
    DateTime EndAt);

public sealed record DealershipAvailability(
    Guid DealershipId,
    string Name,
    IReadOnlyList<string> Slots);

public sealed record AvailabilityResponse(
    Guid ServiceTypeId,
    int DurationMinutes,
    DateOnly Date,
    int SlotGranularityMinutes,
    IReadOnlyList<DealershipAvailability> Dealerships);
