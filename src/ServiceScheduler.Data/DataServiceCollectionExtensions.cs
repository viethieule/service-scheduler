using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceScheduler.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddSchedulerData(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<SchedulerDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }
}
