namespace OnCallApi.Models;

public class PhoneTree
{
    public int Id { get; set; }
    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    public string TreeType { get; set; } = "department"; // emergency, department, oncall, admin, code-blue, code-red, etc.
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string? Procedure { get; set; }
    public string? FallbackProcedure { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PhoneTreeNode> Nodes { get; set; } = new List<PhoneTreeNode>();
}

public class PhoneTreeNode
{
    public int Id { get; set; }
    public int PhoneTreeId { get; set; }
    public PhoneTree? PhoneTree { get; set; }
    public int Order { get; set; }
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public string? RoleName { get; set; } // fallback if not assigned to specific person
    public string? Condition { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
