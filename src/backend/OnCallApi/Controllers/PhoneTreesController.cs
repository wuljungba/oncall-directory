using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/phone-trees")]
[Authorize(Policy = "RequireDirectoryRead")]
public class PhoneTreesController : ControllerBase
{
    private readonly IDirectoryService _directoryService;

    public PhoneTreesController(IDirectoryService directoryService)
    {
        _directoryService = directoryService;
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
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTree>> Update(int id, PhoneTree tree)
    {
        if (id != tree.Id) return BadRequest();
        return await _directoryService.UpdatePhoneTreeAsync(tree);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> Delete(int id)
    {
        await _directoryService.DeletePhoneTreeAsync(id);
        return NoContent();
    }

    [HttpPost("{treeId}/nodes")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<PhoneTreeNode>> AddNode(int treeId, PhoneTreeNode node)
    {
        var created = await _directoryService.AddNodeAsync(treeId, node);
        return CreatedAtAction(nameof(Get), new { id = treeId }, created);
    }

    [HttpPut("nodes/{nodeId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> UpdateNode(int nodeId, PhoneTreeNode node)
    {
        if (nodeId != node.Id) return BadRequest();
        await _directoryService.UpdateNodeAsync(node);
        return NoContent();
    }

    [HttpDelete("nodes/{nodeId}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> RemoveNode(int nodeId)
    {
        await _directoryService.RemoveNodeAsync(nodeId);
        return NoContent();
    }

    [HttpPost("{treeId}/reorder")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> Reorder(int treeId, [FromBody] List<int> nodeIds)
    {
        await _directoryService.ReorderNodesAsync(treeId, nodeIds);
        return NoContent();
    }
}
