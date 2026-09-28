namespace ServiceScheduler.Services;

/// <summary>
/// Placeholders standing in for data the design puts out of scope. Per-dealership
/// opening hours would replace the business day; without it the candidate grid would
/// run midnight to midnight and offer 03:00 appointments.
/// </summary>
public static class SchedulingConstants
{
    public const int SlotGranularityMinutes = 30;

    public static readonly TimeOnly BusinessDayStart = new(8, 0);
    public static readonly TimeOnly BusinessDayEnd = new(18, 0);

    /// <summary>
    /// How long a booking waits for the dealership-day lock before giving up. Without a
    /// bound, waiters queue without limit while holding pooled connections, so one busy
    /// dealership could exhaust the pool and affect every other dealership.
    /// </summary>
    public const string BookingLockTimeout = "2s";
}
