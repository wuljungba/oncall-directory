using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/escalation")]
[Authorize(Policy = "RequireScheduleRead")]
public class EscalationController : ControllerBase
{
    private readonly EscalationService _escalationService;
    private readonly ITenantBroadcaster _broadcast;

    public EscalationController(EscalationService escalationService, ITenantBroadcaster broadcast)
    {
        _escalationService = escalationService;
        _broadcast = broadcast;
    }

    // ── Policies ──

    /// <summary>Get all escalation policies, optionally filtered by department.</summary>
    [HttpGet("policies")]
    public async Task<ActionResult<List<EscalationPolicy>>> GetPolicies([FromQuery] int? departmentId)
    {
        return await _escalationService.GetPoliciesAsync(departmentId);
    }

    /// <summary>Create a new escalation policy.</summary>
    [HttpPost("policies")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<EscalationPolicy>> CreatePolicy(EscalationPolicy policy)
    {
        var created = await _escalationService.CreatePolicyAsync(policy);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForDepartmentAsync(created.DepartmentId),
            "EscalationPolicyUpdated", created, safetyCritical: true);
        return CreatedAtAction(nameof(GetPolicies), new { id = created.Id }, created);
    }

    /// <summary>Update an existing escalation policy.</summary>
    [HttpPut("policies/{id}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<EscalationPolicy>> UpdatePolicy(int id, EscalationPolicy policy)
    {
        if (id != policy.Id)
            return BadRequest(new { error = "Route ID and body ID must match." });

        var updated = await _escalationService.UpdatePolicyAsync(policy);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForDepartmentAsync(updated.DepartmentId),
            "EscalationPolicyUpdated", updated, safetyCritical: true);
        return Ok(updated);
    }

    /// <summary>Delete an escalation policy.</summary>
    [HttpDelete("policies/{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> DeletePolicy(int id)
    {
        // Resolved before the delete, while the row that carries the tenant still exists.
        var policyTenantId = await _broadcast.TenantForEscalationPolicyAsync(id);
        await _escalationService.DeletePolicyAsync(id);
        await _broadcast.ToTenantAsync(
            policyTenantId, "EscalationPolicyUpdated", new { id, deleted = true }, safetyCritical: true);
        return NoContent();
    }

    // ── Events ──

    /// <summary>Get escalation events, optionally filtered by policy.</summary>
    [HttpGet("events")]
    public async Task<ActionResult<List<EscalationEvent>>> GetEvents([FromQuery] int? policyId, [FromQuery] int? limit)
    {
        return await _escalationService.GetEventsAsync(policyId, limit);
    }

    /// <summary>
    /// Acknowledge/resolve an escalation event. Once acknowledged, no further
    /// escalation tiers will fire for the associated shift.
    /// </summary>
    [HttpPost("events/{id}/acknowledge")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<EscalationEvent>> AcknowledgeEvent(int id)
    {
        try
        {
            var escEvent = await _escalationService.AcknowledgeEventAsync(id);
            await _broadcast.ToTenantAsync(
                await _broadcast.TenantForEscalationPolicyAsync(escEvent.PolicyId),
                "EscalationEventUpdated", escEvent, safetyCritical: true);
            return Ok(escEvent);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
