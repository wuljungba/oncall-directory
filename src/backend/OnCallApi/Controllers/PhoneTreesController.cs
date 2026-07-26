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
    private readonly IHubContext<OnCallNotificationHub> _hub;

    public PhoneTreesController(IDirectoryService directoryService, IHubContext<OnCallNotificationHub> hub)
    {
        _directoryService = directoryService;
        _hub = hub;
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
        await _hub.Clients.All.SendAsync("PhoneTreeCreated", created);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTree>> Update(int id, PhoneTree tree)
    {
        if (id != tree.Id) return BadRequest();
        var updated = await _directoryService.UpdatePhoneTreeAsync(tree);
        await _hub.Clients.All.SendAsync("PhoneTreeUpdated", updated);
        return updated;
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> Delete(int id)
    {
        await _directoryService.DeletePhoneTreeAsync(id);
        await _hub.Clients.All.SendAsync("PhoneTreeDeleted", new { id });
        return NoContent();
    }

    [HttpPost("{treeId}/nodes")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeNode>> AddNode(int treeId, PhoneTreeNode node)
    {
        var created = await _directoryService.AddNodeAsync(treeId, node);
        await _hub.Clients.All.SendAsync("PhoneTreeUpdated", new { treeId, action = "nodeAdded" });
        return CreatedAtAction(nameof(Get), new { id = treeId }, created);
    }

    [HttpPut("nodes/{nodeId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> UpdateNode(int nodeId, PhoneTreeNode node)
    {
        if (nodeId != node.Id) return BadRequest();
        await _directoryService.UpdateNodeAsync(node);
        await _hub.Clients.All.SendAsync("PhoneTreeUpdated", new { nodeId, action = "nodeUpdated" });
        return NoContent();
    }

    [HttpDelete("nodes/{nodeId}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> RemoveNode(int nodeId)
    {
        await _directoryService.RemoveNodeAsync(nodeId);
        await _hub.Clients.All.SendAsync("PhoneTreeUpdated", new { nodeId, action = "nodeRemoved" });
        return NoContent();
    }

    [HttpPost("{treeId}/reorder")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> Reorder(int treeId, [FromBody] List<int> nodeIds)
    {
        await _directoryService.ReorderNodesAsync(treeId, nodeIds);
        await _hub.Clients.All.SendAsync("PhoneTreeUpdated", new { treeId, action = "reordered" });
        return NoContent();
    }
}
