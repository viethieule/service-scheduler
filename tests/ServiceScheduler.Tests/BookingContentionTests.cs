using Microsoft.EntityFrameworkCore;
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
        new BookingService(db, new TestServiceContext(SeedData.CustomerId));

    /// <summary>
    /// The safety property: concurrency never confirms more than there is capacity, and a
    /// caller either succeeds or is told there is none.
    ///
    /// It deliberately does not require capacity to be fully used. Assignment picks one
    /// candidate bay and technician without retrying, so under tight concurrency every
    /// caller can choose the same pair and all but one lose, even though another pair was
    /// free. That is under-use, not over-booking, and closing it is the contention work.
    /// </summary>
    [Fact]
    public async Task Concurrent_bookings_for_one_slot_never_exceed_capacity()
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

        Assert.InRange(confirmed, 1, seededCapacity);
        Assert.Equal(attempts - confirmed, rejected);

        // Whatever was confirmed must sit on distinct resources.
        var winners = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();
        Assert.Equal(confirmed, winners.Select(w => w.ServiceBayId).Distinct().Count());
        Assert.Equal(confirmed, winners.Select(w => w.TechnicianId).Distinct().Count());
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
        var service = new BookingService(db, new TestServiceContext(customerId: 999));

        var result = await service.CreateBookingAsync(new CreateBookingCommand(
            SeedData.DealershipId,
            SeedData.VehicleId,
            SeedData.OilChangeId,
            NextWeekdayAt(16)));

        Assert.False(result.IsSuccess);
        Assert.Equal(BookingError.VehicleNotOwned, result.Error);
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
