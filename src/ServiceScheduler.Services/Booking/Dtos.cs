namespace ServiceScheduler.Services.Booking;

public sealed record CreateBookingCommand(
    int DealershipId,
    int VehicleId,
    int ServiceTypeId,
    DateTime StartAt);

public sealed record BookingConfirmation(
    int BookingId,
    int DealershipId,
    int VehicleId,
    int ServiceTypeId,
    int TechnicianId,
    int ServiceBayId,
    DateTime StartAt,
    DateTime EndAt);

public sealed record DealershipAvailability(
    int DealershipId,
    string Name,
    IReadOnlyList<string> Slots);

public sealed record AvailabilityResponse(
    int ServiceTypeId,
    int DurationMinutes,
    DateOnly Date,
    int SlotGranularityMinutes,
    IReadOnlyList<DealershipAvailability> Dealerships);
