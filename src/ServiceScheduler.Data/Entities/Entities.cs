namespace ServiceScheduler.Data.Entities;

public class Dealership
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;

    public List<ServiceBay> ServiceBays { get; set; } = [];
    public List<Technician> Technicians { get; set; } = [];
}

public class ServiceBay
{
    public int Id { get; set; }
    public int DealershipId { get; set; }
    public string Name { get; set; } = string.Empty;

    public Dealership? Dealership { get; set; }
}

public class Technician
{
    public int Id { get; set; }
    public int DealershipId { get; set; }
    public string Name { get; set; } = string.Empty;

    public Dealership? Dealership { get; set; }
}

public class ServiceType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
}

public class Vehicle
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Vin { get; set; } = string.Empty;
    public string Make { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string FuelType { get; set; } = string.Empty;

    public Customer? Customer { get; set; }
}

/// <summary>
/// Stored as an int because the overlap constraints in the initial migration are
/// partial on <c>status = 1</c>, so a cancelled appointment stops blocking its slot.
/// </summary>
public enum AppointmentStatus
{
    Confirmed = 1,
    Cancelled = 2
}

public class Appointment
{
    public int Id { get; set; }
    public int DealershipId { get; set; }
    public int CustomerId { get; set; }
    public int VehicleId { get; set; }
    public int ServiceTypeId { get; set; }
    public int TechnicianId { get; set; }
    public int ServiceBayId { get; set; }

    /// <summary>Inclusive start, UTC.</summary>
    public DateTime StartAt { get; set; }

    /// <summary>Exclusive end, UTC. An appointment ending at 10:00 does not
    /// collide with one starting at 10:00.</summary>
    public DateTime EndAt { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;
    public DateTime CreatedAt { get; set; }

    public Dealership? Dealership { get; set; }
    public Customer? Customer { get; set; }
    public Vehicle? Vehicle { get; set; }
    public ServiceType? ServiceType { get; set; }
    public Technician? Technician { get; set; }
    public ServiceBay? ServiceBay { get; set; }
}
