namespace OnCallApi.Models;

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AzureAdObjectId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Specialty { get; set; }
    public string? ClinicalRole { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? OfficePhone { get; set; }
    public string? MobilePhone { get; set; }
    public string? PagerNumber { get; set; }
    public string? OfficeLocation { get; set; }
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid? ManagerId { get; set; }
    public Employee? Manager { get; set; }
    public string? Certifications { get; set; } // JSON array
    public string? Languages { get; set; } // JSON array
    public bool OnCallStatus { get; set; }
    public string Presence { get; set; } = "unknown";
    public bool IsActive { get; set; } = true;
    public DateTime LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Employee> DirectReports { get; set; } = new List<Employee>();
    public ICollection<Shift> Shifts { get; set; } = new List<Shift>();
    public ICollection<ShiftSwap> SwapRequests { get; set; } = new List<ShiftSwap>();
}
