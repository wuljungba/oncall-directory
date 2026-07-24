using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireDirectoryRead")]
public class DirectoryController : ControllerBase
{
    private readonly IDirectoryService _directoryService;

    public DirectoryController(IDirectoryService directoryService)
    {
        _directoryService = directoryService;
    }

    /// <summary>Search employees by name, title, or email.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<Employee>>> Search([FromQuery] string? q, [FromQuery] int? departmentId)
    {
        if (string.IsNullOrWhiteSpace(q) && !departmentId.HasValue)
            return await _directoryService.SearchEmployeesAsync("");

        return await _directoryService.SearchEmployeesAsync(q ?? "", departmentId);
    }

    /// <summary>Get all employees in a department.</summary>
    [HttpGet("department/{departmentId}")]
    public async Task<ActionResult<List<Employee>>> GetDepartment(int departmentId)
    {
        return await _directoryService.GetDepartmentEmployeesAsync(departmentId);
    }

    /// <summary>Get employee by ID.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Employee>> GetEmployee(Guid id)
    {
        var employee = await _directoryService.GetEmployeeByIdAsync(id);
        if (employee == null) return NotFound();
        return employee;
    }

    /// <summary>Get employee by email.</summary>
    [HttpGet("by-email/{email}")]
    public async Task<ActionResult<Employee>> GetByEmail(string email)
    {
        var employee = await _directoryService.GetEmployeeByEmailAsync(email);
        if (employee == null) return NotFound();
        return employee;
    }

    /// <summary>Get phone trees.</summary>
    [HttpGet("phone-trees")]
    public async Task<ActionResult<List<PhoneTree>>> GetPhoneTrees([FromQuery] int? departmentId)
    {
        return await _directoryService.GetPhoneTreesAsync(departmentId);
    }

    /// <summary>Get a specific phone tree.</summary>
    [HttpGet("phone-trees/{id}")]
    public async Task<ActionResult<PhoneTree>> GetPhoneTree(int id)
    {
        var tree = await _directoryService.GetPhoneTreeByIdAsync(id);
        if (tree == null) return NotFound();
        return tree;
    }

    /// <summary>Get currently on-call employees.</summary>
    [HttpGet("on-call")]
    public async Task<ActionResult<List<Employee>>> GetOnCallEmployees([FromQuery] int? departmentId)
    {
        return await _directoryService.GetOnCallEmployeesAsync(departmentId);
    }
}
