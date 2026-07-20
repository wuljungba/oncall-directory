using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Producer interface for asynchronous HIPAA audit logging.
/// Enqueues audit events; a background service batch-inserts them.
/// </summary>
public interface IAuditService
{
    void Enqueue(AuditLog log);
}
