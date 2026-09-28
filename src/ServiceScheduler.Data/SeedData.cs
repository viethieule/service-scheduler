using ServiceScheduler.Data.Entities;

namespace ServiceScheduler.Data;

/// <summary>
/// Fixed development data. Two service bays and two technicians are deliberate:
/// a burst of concurrent bookings at one start time must produce exactly two
/// confirmations, which is a checkable number rather than "one of them wins".
/// </summary>
public static class SeedData
{
    public static readonly Guid DealershipId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid BayOneId     = Guid.Parse("22222222-2222-2222-2222-222222222201");
    public static readonly Guid BayTwoId     = Guid.Parse("22222222-2222-2222-2222-222222222202");
    public static readonly Guid TechOneId    = Guid.Parse("33333333-3333-3333-3333-333333333301");
    public static readonly Guid TechTwoId    = Guid.Parse("33333333-3333-3333-3333-333333333302");
    public static readonly Guid CustomerId   = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid VehicleId    = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static readonly Guid OilChangeId    = Guid.Parse("66666666-6666-6666-6666-666666666601");
    public static readonly Guid TyreChangeId   = Guid.Parse("66666666-6666-6666-6666-666666666602");
    public static readonly Guid DiagnosticId   = Guid.Parse("66666666-6666-6666-6666-666666666603");
    public static readonly Guid BrakeServiceId = Guid.Parse("66666666-6666-6666-6666-666666666604");

    public static async Task EnsureSeededAsync(SchedulerDbContext db, CancellationToken ct = default)
    {
        if (db.Dealerships.Any()) return;

        db.Dealerships.Add(new Dealership
        {
            Id = DealershipId,
            Name = "Northside Motors",
            Address = "12 Industrial Road"
        });

        db.ServiceBays.AddRange(
            new ServiceBay { Id = BayOneId, DealershipId = DealershipId, Name = "Bay 1" },
            new ServiceBay { Id = BayTwoId, DealershipId = DealershipId, Name = "Bay 2" });

        db.Technicians.AddRange(
            new Technician { Id = TechOneId, DealershipId = DealershipId, Name = "A. Patel" },
            new Technician { Id = TechTwoId, DealershipId = DealershipId, Name = "M. Nguyen" });

        db.ServiceTypes.AddRange(
            new ServiceType { Id = OilChangeId,    Name = "Oil change",    DurationMinutes = 60 },
            new ServiceType { Id = TyreChangeId,   Name = "Tyre change",   DurationMinutes = 60 },
            new ServiceType { Id = DiagnosticId,   Name = "Diagnostic",    DurationMinutes = 90 },
            new ServiceType { Id = BrakeServiceId, Name = "Brake service", DurationMinutes = 120 });

        db.Customers.Add(new Customer
        {
            Id = CustomerId,
            Name = "Hieu Le",
            Email = "owner@example.com",
            Phone = "+84 000 000 000"
        });

        db.Vehicles.Add(new Vehicle
        {
            Id = VehicleId,
            CustomerId = CustomerId,
            Vin = "WVWZZZ1JZXW000001",
            Make = "Volkswagen",
            Model = "Golf",
            Year = 2019,
            FuelType = "Petrol"
        });

        await db.SaveChangesAsync(ct);
    }
}
