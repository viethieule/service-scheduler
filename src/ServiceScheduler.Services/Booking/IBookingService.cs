using ServiceScheduler.Shared;

namespace ServiceScheduler.Services.Booking;

public interface IBookingService
{
    /// <summary>
    /// Bookable start times for the given service type and UTC date. Advisory: it takes
    /// no locks and reserves nothing, so a returned slot may be gone by the time the
    /// caller acts on it.
    /// </summary>
    Task<Result<AvailabilityResponse>> GetAvailabilityAsync(
        int serviceTypeId,
        DateOnly date,
        CancellationToken ct = default);

    /// <summary>
    /// Assigns a free service bay and technician and persists the appointment.
    /// Authoritative: the database rejects any overlap.
    /// </summary>
    Task<Result<BookingConfirmation>> CreateBookingAsync(
        CreateBookingCommand command,
        CancellationToken ct = default);
}
