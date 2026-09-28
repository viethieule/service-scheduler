using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceScheduler.Data;
using ServiceScheduler.Services.Booking;
using ServiceScheduler.Shared;

namespace ServiceScheduler.Tests;

[Collection(nameof(SchedulerCollection))]
public class BookingContentionTests(SchedulerFixture fixture)
{
    private static DateTime NextWeekdayAt(int hour) =>
        DateTime.SpecifyKind(
            DateTime.UtcNow.Date.AddDays(7).AddHours(hour), DateTimeKind.Utc);

    private IBookingService NewService(SchedulerDbContext db) =>
        new BookingService(db, new TestServiceContext(SeedData.CustomerId), NullLogger<BookingService>.Instance);

    /// <summary>
    /// With the dealership-day advisory lock in place this is deterministic: every caller
    /// reads committed state under the lock, so exactly the seeded capacity is confirmed
    /// and the rest are correctly told there is none. Before the lock the outcome varied
    /// between one and two, because callers raced and losers were rejected although
    /// another pair was free.
    /// </summary>
    [Fact]
    public async Task Concurrent_bookings_for_one_slot_confirm_exactly_the_seeded_capacity()
    {
        const int attempts = 20;
        const int seededCapacity = 2; // two service bays, two technicians

        var startAt = NextWeekdayAt(9);

        var command = new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.BrakeServiceId,
            startAt);

        // Each caller gets its own DbContext, as it would per request.
        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await NewService(db).CreateBookingAsync(command);
        });

        var results = await Task.WhenAll(tasks);

        var confirmed = results.Count(r => r.IsSuccess);
        var rejected = results.Count(r => r.Error == BookingError.NoCapacity);

        Assert.Equal(seededCapacity, confirmed);
        Assert.Equal(attempts - seededCapacity, rejected);

        // Every confirmation must sit on a distinct bay and a distinct technician.
        var winners = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();
        Assert.Equal(seededCapacity, winners.Select(w => w.ServiceBayId).Distinct().Count());
        Assert.Equal(seededCapacity, winners.Select(w => w.TechnicianId).Distinct().Count());
    }

    [Fact]
    public async Task No_two_confirmed_appointments_overlap_on_a_resource()
    {
        var startAt = NextWeekdayAt(11);

        var command = new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.OilChangeId,
            startAt);

        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await NewService(db).CreateBookingAsync(command);
        });

        await Task.WhenAll(tasks);

        await using var check = fixture.CreateContext();

        var overlaps = await check.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM appointments a
                JOIN appointments b
                  ON a.id <> b.id
                 AND (a.technician_id = b.technician_id OR a.service_bay_id = b.service_bay_id)
                 AND tstzrange(a.start_at, a.end_at, '[)') && tstzrange(b.start_at, b.end_at, '[)')
                WHERE a.status = 1 AND b.status = 1
                """)
            .SingleAsync();

        Assert.Equal(0, overlaps);
    }

    [Fact]
    public async Task Adjacent_appointments_do_not_collide()
    {
        var first = NextWeekdayAt(14);

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        // Oil change is 60 minutes, so the second starts exactly when the first ends.
        var a = await service.CreateBookingAsync(
            new CreateBookingCommand(SeedData.DealershipId, SeedData.VehicleId,
                SeedData.OilChangeId, first));

        var b = await service.CreateBookingAsync(
            new CreateBookingCommand(SeedData.DealershipId, SeedData.VehicleId,
                SeedData.OilChangeId, first.AddHours(1)));

        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);
    }

    [Fact]
    public async Task Booking_a_vehicle_owned_by_someone_else_is_rejected()
    {
        await using var db = fixture.CreateContext();
        var service = new BookingService(db, new TestServiceContext(customerId: 999), NullLogger<BookingService>.Instance);

        var result = await service.CreateBookingAsync(new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.OilChangeId,
            NextWeekdayAt(16)));

        Assert.False(result.IsSuccess);
        Assert.Equal(BookingError.VehicleNotOwned, result.Error);
    }

    [Fact]
    public async Task Booking_outside_the_business_day_is_rejected()
    {
        await using var db = fixture.CreateContext();

        // 23:00 with a 120-minute service would cross midnight, which the single
        // dealership-day lock cannot cover.
        var result = await NewService(db).CreateBookingAsync(new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.BrakeServiceId,
            NextWeekdayAt(23)));

        Assert.False(result.IsSuccess);
        Assert.Equal(BookingError.OutsideBusinessHours, result.Error);
    }

    [Fact]
    public async Task Booking_that_would_run_past_closing_is_rejected()
    {
        await using var db = fixture.CreateContext();

        // 17:00 + 120 minutes ends at 19:00, an hour after closing.
        var result = await NewService(db).CreateBookingAsync(new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.BrakeServiceId,
            NextWeekdayAt(17)));

        Assert.False(result.IsSuccess);
        Assert.Equal(BookingError.OutsideBusinessHours, result.Error);
    }

    [Fact]
    public async Task Booking_in_the_past_is_rejected()
    {
        await using var db = fixture.CreateContext();

        var result = await NewService(db).CreateBookingAsync(new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.OilChangeId,
            DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-3).AddHours(9), DateTimeKind.Utc)));

        Assert.False(result.IsSuccess);
        Assert.Equal(BookingError.DateInPast, result.Error);
    }
}
