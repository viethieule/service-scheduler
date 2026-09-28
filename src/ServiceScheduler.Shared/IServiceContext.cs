namespace ServiceScheduler.Shared;

/// <summary>
/// Ambient information about the caller, supplied by the hosting layer.
/// Keeps the service layer free of any dependency on ASP.NET Core.
/// </summary>
public interface IServiceContext
{
    Guid CustomerId { get; }

    string CorrelationId { get; }
}
