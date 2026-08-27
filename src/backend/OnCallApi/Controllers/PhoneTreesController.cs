using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/phone-trees")]
[Authorize(Policy = "RequireDirectoryRead")]
public class PhoneTreesController : ControllerBase
{
    private readonly IDirectoryService _directoryService;
    private readonly ITenantBroadcaster _broadcast;

    public PhoneTreesController(IDirectoryService directoryService, ITenantBroadcaster broadcast)
    {
        _directoryService = directoryService;
        _broadcast = broadcast;
    }

    [HttpGet]
    public async Task<ActionResult<List<PhoneTree>>> GetAll([FromQuery] int? departmentId)
    {
        return await _directoryService.GetPhoneTreesAsync(departmentId);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PhoneTree>> Get(int id)
    {
        var tree = await _directoryService.GetPhoneTreeByIdAsync(id);
        if (tree == null) return NotFound();
        return tree;
    }

    [HttpPost]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTree>> Create(PhoneTree tree)
    {
        var created = await _directoryService.CreatePhoneTreeAsync(tree);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForPhoneTreeAsync(created.Id), "PhoneTreeCreated", created, safetyCritical: true);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTree>> Update(int id, PhoneTree tree)
    {
        if (id != tree.Id) return BadRequest();
        var updated = await _directoryService.UpdatePhoneTreeAsync(tree);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForPhoneTreeAsync(updated.Id), "PhoneTreeUpdated", updated, safetyCritical: true);
        return updated;
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> Delete(int id)
    {
        // Resolved before the delete, while the row that carries the tenant still exists.
        var deletedTenantId = await _broadcast.TenantForPhoneTreeAsync(id);
        await _directoryService.DeletePhoneTreeAsync(id);
        await _broadcast.ToTenantAsync(deletedTenantId, "PhoneTreeDeleted", new { id }, safetyCritical: true);
        return NoContent();
    }

    [HttpPost("{treeId}/nodes")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeNode>> AddNode(int treeId, PhoneTreeNode node)
    {
        var created = await _directoryService.AddNodeAsync(treeId, node);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForPhoneTreeAsync(treeId),
            "PhoneTreeUpdated", new { treeId, action = "nodeAdded" }, safetyCritical: true);
        return CreatedAtAction(nameof(Get), new { id = treeId }, created);
    }

    [HttpPut("nodes/{nodeId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> UpdateNode(int nodeId, PhoneTreeNode node)
    {
        if (nodeId != node.Id) return BadRequest();
        await _directoryService.UpdateNodeAsync(node);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForPhoneTreeNodeAsync(nodeId),
            "PhoneTreeUpdated", new { nodeId, action = "nodeUpdated" }, safetyCritical: true);
        return NoContent();
    }

    [HttpDelete("nodes/{nodeId}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> RemoveNode(int nodeId)
    {
        // Resolved before the delete, while the row that carries the tenant still exists.
        var nodeTenantId = await _broadcast.TenantForPhoneTreeNodeAsync(nodeId);
        await _directoryService.RemoveNodeAsync(nodeId);
        await _broadcast.ToTenantAsync(
            nodeTenantId, "PhoneTreeUpdated", new { nodeId, action = "nodeRemoved" }, safetyCritical: true);
        return NoContent();
    }

    [HttpPost("{treeId}/reorder")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> Reorder(int treeId, [FromBody] List<int> nodeIds)
    {
        await _directoryService.ReorderNodesAsync(treeId, nodeIds);
        await _broadcast.ToTenantAsync(
            await _broadcast.TenantForPhoneTreeAsync(treeId),
            "PhoneTreeUpdated", new { treeId, action = "reordered" }, safetyCritical: true);
        return NoContent();
    }
}
