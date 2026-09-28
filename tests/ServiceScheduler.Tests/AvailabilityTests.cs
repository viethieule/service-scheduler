using ServiceScheduler.Data;
using ServiceScheduler.Services;
using ServiceScheduler.Services.Booking;
using ServiceScheduler.Shared;

namespace ServiceScheduler.Tests;

[Collection(nameof(SchedulerCollection))]
public class AvailabilityTests(SchedulerFixture fixture)
{
    private IBookingService NewService(Data.SchedulerDbContext db) =>
        new BookingService(db, new TestServiceContext(SeedData.CustomerId));

    [Fact]
    public async Task Unknown_service_type_is_rejected()
    {
        await using var db = fixture.CreateContext();

        var result = await NewService(db).GetAvailabilityAsync(
            Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));

        Assert.False(result.IsSuccess);
        Assert.Equal(BookingError.ServiceTypeNotFound, result.Error);
    }

    [Fact]
    public async Task Slots_sit_on_the_grid_and_end_within_the_business_day()
    {
        await using var db = fixture.CreateContext();

        // A clear date, far from anything the contention tests book.
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60));

        var result = await NewService(db).GetAvailabilityAsync(SeedData.BrakeServiceId, date);

        Assert.True(result.IsSuccess);
        var dealership = Assert.Single(result.Value!.Dealerships);

        Assert.Equal(120, result.Value!.DurationMinutes);
        Assert.Equal(SchedulingConstants.SlotGranularityMinutes,
                     result.Value!.SlotGranularityMinutes);

        var slots = dealership.Slots.Select(TimeOnly.Parse).ToList();

        Assert.Equal(SchedulingConstants.BusinessDayStart, slots.First());

        // Every slot lands on the 30-minute grid.
        Assert.All(slots, s => Assert.Equal(0, s.Minute % SchedulingConstants.SlotGranularityMinutes));

        // A 120-minute service must finish by 18:00, so the last start is 16:00.
        Assert.Equal(
            SchedulingConstants.BusinessDayEnd.AddMinutes(-120),
            slots.Last());
    }

    [Fact]
    public async Task A_booking_removes_only_the_slots_it_overlaps()
    {
        await using var db = fixture.CreateContext();
        var service = NewService(db);

        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90));
        var startAt = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Utc);

        var before = await service.GetAvailabilityAsync(SeedData.OilChangeId, date);
        Assert.Contains("09:00", before.Value!.Dealerships.Single().Slots);

        // Consume both bays at 09:00-10:00, so that hour has no capacity left.
        for (var i = 0; i < 2; i++)
        {
            var booked = await service.CreateBookingAsync(new CreateBookingCommand(
                SeedData.DealershipId, SeedData.VehicleId, SeedData.OilChangeId, startAt));
            Assert.True(booked.IsSuccess);
        }

        var after = await service.GetAvailabilityAsync(SeedData.OilChangeId, date);
        var slots = after.Value!.Dealerships.Single().Slots;

        Assert.DoesNotContain("09:00", slots);
        Assert.DoesNotContain("09:30", slots);   // a 60-minute service here would overlap
        Assert.Contains("08:00", slots);         // ends exactly at 09:00, so it still fits
        Assert.Contains("10:00", slots);         // starts exactly when the bookings end
    }
}
