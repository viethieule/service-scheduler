using Microsoft.EntityFrameworkCore;
using ServiceScheduler.Data.Entities;

namespace ServiceScheduler.Data;

/// <summary>
/// Fixed development data. Two service bays and two technicians are deliberate:
/// a burst of concurrent bookings at one start time must produce exactly two
/// confirmations, which is a checkable number rather than "one of them wins".
/// </summary>
public static class SeedData
{
    public const int DealershipId = 1;
    public const int BayOneId = 1;
    public const int BayTwoId = 2;
    public const int TechOneId = 1;
    public const int TechTwoId = 2;
    public const int CustomerId = 1;
    public const int VehicleId = 1;

    public const int OilChangeId = 1;
    public const int TyreChangeId = 2;
    public const int DiagnosticId = 3;
    public const int BrakeServiceId = 4;

    /// <summary>Tables seeded with explicit keys, whose identity sequences must be advanced.</summary>
    private static readonly string[] SeededTables =
        ["dealerships", "service_bays", "technicians", "service_types", "customers", "vehicles"];

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
        await AdvanceIdentitySequencesAsync(db, ct);
    }

    /// <summary>
    /// Seeding writes explicit keys, which leaves each identity sequence still at 1.
    /// Without this, the next generated key would collide with a seeded row.
    /// </summary>
    private static async Task AdvanceIdentitySequencesAsync(
        SchedulerDbContext db, CancellationToken ct)
    {
        foreach (var table in SeededTables)
        {
            // Table names come from the fixed list above, never from input.
            var sql = "SELECT setval(pg_get_serial_sequence('" + table + "', 'id'), "
                    + "(SELECT COALESCE(MAX(id), 1) FROM " + table + "));";

            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }
}
