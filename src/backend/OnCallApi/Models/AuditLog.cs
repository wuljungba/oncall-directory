namespace OnCallApi.Models;

public class AuditLog
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // Created, Read, Updated, Deleted, Exported
    public string ResourceType { get; set; } = string.Empty; // Employee, Schedule, Shift, etc.
    public string? ResourceId { get; set; }
    public string? Details { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
