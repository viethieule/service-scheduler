using System.Diagnostics;
using ServiceScheduler.Data;
using ServiceScheduler.Shared;

namespace ServiceScheduler.API.ServiceContext;

/// <summary>
/// Stands in for authentication, which is out of scope. The caller may supply
/// <c>X-Customer-Id</c>; otherwise the seeded development customer is assumed.
/// Confining this to the hosting layer keeps the service layer free of HttpContext.
/// </summary>
public sealed class HeaderServiceContext(IHttpContextAccessor accessor) : IServiceContext
{
    public int CustomerId
    {
        get
        {
            var header = accessor.HttpContext?.Request.Headers["X-Customer-Id"].FirstOrDefault();
            return int.TryParse(header, out var id) ? id : SeedData.CustomerId;
        }
    }

    /// <summary>
    /// The W3C trace id, so the value shown to a caller is the same one that identifies the
    /// request in logs and in a tracing backend. <c>HttpContext.TraceIdentifier</c> would be
    /// local to this process and mean nothing outside it.
    /// </summary>
    public string CorrelationId =>
        Activity.Current?.TraceId.ToString()
        ?? accessor.HttpContext?.TraceIdentifier
        ?? string.Empty;
}
