using Microsoft.EntityFrameworkCore;
using ServiceScheduler.API.Endpoints;
using ServiceScheduler.API.OpenApi;
using ServiceScheduler.API.ServiceContext;
using ServiceScheduler.Data;
using ServiceScheduler.Services.Booking;
using ServiceScheduler.Shared;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Scheduler")
    ?? throw new InvalidOperationException("Connection string 'Scheduler' is not configured.");

builder.Services.AddSchedulerData(connectionString);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IServiceContext, HeaderServiceContext>();
builder.Services.AddScoped<IBookingService, BookingService>();

// Swagger UI speaks OpenAPI 3.0, and cannot render the "integer or string" union that
// .NET 10 gives a simple query parameter. See FlattenScalarUnionsTransformer.
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;
    options.AddSchemaTransformer<FlattenScalarUnionsTransformer>();
});
builder.Services.AddProblemDetails();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();
        await db.Database.MigrateAsync();
        await SeedData.EnsureSeededAsync(db);
    }

    // .NET 10 serves the OpenAPI document but ships no UI, so Swashbuckle
    // supplies the UI alone, pointed at that document.
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Service Scheduler v1"));
}

app.UseExceptionHandler();
app.MapBookingEndpoints();

app.Run();

public partial class Program;
