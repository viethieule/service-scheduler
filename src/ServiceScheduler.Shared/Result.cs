namespace ServiceScheduler.Shared;

/// <summary>
/// Why an operation did not succeed. The hosting layer decides how each one maps
/// onto a transport status code.
/// </summary>
public enum BookingError
{
    None = 0,
    ServiceTypeNotFound,
    DealershipNotFound,
    VehicleNotFound,
    VehicleNotOwned,
    DateInPast,

    /// <summary>
    /// The requested window does not lie entirely within the business day.
    /// </summary>
    OutsideBusinessHours,

    /// <summary>
    /// No service bay or no technician was free for the whole requested window.
    /// </summary>
    NoCapacity,

    /// <summary>
    /// The dealership's schedule was locked by another booking for longer than the wait
    /// allows. Distinct from <see cref="NoCapacity"/>: capacity was never determined, so
    /// the caller should retry rather than be told there is none.
    /// </summary>
    Busy
}

/// <summary>
/// Outcome of a service call: a value, or a reason it could not be produced.
/// Returned rather than thrown, so the service layer needs no knowledge of HTTP.
/// </summary>
public sealed record Result<T>
{
    private Result(T? value, BookingError error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public BookingError Error { get; }

    public bool IsSuccess => Error == BookingError.None;

    public static Result<T> Ok(T value) => new(value, BookingError.None);

    public static Result<T> Fail(BookingError error) => new(default, error);
}
