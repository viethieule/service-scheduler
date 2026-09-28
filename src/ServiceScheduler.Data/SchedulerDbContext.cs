using Microsoft.EntityFrameworkCore;
using ServiceScheduler.Data.Entities;

namespace ServiceScheduler.Data;

public class SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : DbContext(options)
{
    public DbSet<Dealership> Dealerships => Set<Dealership>();
    public DbSet<ServiceBay> ServiceBays => Set<ServiceBay>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<ServiceType> ServiceTypes => Set<ServiceType>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Dealership>(e =>
        {
            e.ToTable("dealerships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Address).HasMaxLength(400);
        });

        b.Entity<ServiceBay>(e =>
        {
            e.ToTable("service_bays");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Dealership).WithMany(d => d.ServiceBays).HasForeignKey(x => x.DealershipId);
            e.HasIndex(x => x.DealershipId);
        });

        b.Entity<Technician>(e =>
        {
            e.ToTable("technicians");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Dealership).WithMany(d => d.Technicians).HasForeignKey(x => x.DealershipId);
            e.HasIndex(x => x.DealershipId);
        });

        b.Entity<ServiceType>(e =>
        {
            e.ToTable("service_types");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.DurationMinutes).IsRequired();
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(50);
        });

        b.Entity<Vehicle>(e =>
        {
            e.ToTable("vehicles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Vin).HasMaxLength(17).IsRequired();
            e.HasIndex(x => x.Vin).IsUnique();
            e.Property(x => x.Make).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.FuelType).HasMaxLength(50);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasIndex(x => x.CustomerId);
        });

        b.Entity<Appointment>(e =>
        {
            e.ToTable("appointments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.StartAt).HasColumnType("timestamptz").IsRequired();
            e.Property(x => x.EndAt).HasColumnType("timestamptz").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamptz").IsRequired();

            e.HasOne(x => x.Dealership).WithMany().HasForeignKey(x => x.DealershipId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId);
            e.HasOne(x => x.ServiceType).WithMany().HasForeignKey(x => x.ServiceTypeId);
            e.HasOne(x => x.Technician).WithMany().HasForeignKey(x => x.TechnicianId);
            e.HasOne(x => x.ServiceBay).WithMany().HasForeignKey(x => x.ServiceBayId);

            // Supports the per-day availability query.
            e.HasIndex(x => new { x.DealershipId, x.StartAt });
        });

        // Snake-case every column, so the hand-written exclusion constraints in the
        // initial migration read the same as the schema.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
