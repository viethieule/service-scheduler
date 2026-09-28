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

    public string CorrelationId =>
        accessor.HttpContext?.TraceIdentifier ?? Guid.CreateVersion7().ToString();
}
