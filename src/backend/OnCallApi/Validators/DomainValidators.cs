using FluentValidation;
using OnCallApi.Models;

namespace OnCallApi.Validators;

public class DepartmentValidator : AbstractValidator<Department>
{
    public DepartmentValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Department name is required.")
            .MaximumLength(100).WithMessage("Department name cannot exceed 100 characters.");
    }
}

public class DutyHourRuleValidator : AbstractValidator<DutyHourRule>
{
    public DutyHourRuleValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Rule name is required.")
            .MaximumLength(200).WithMessage("Rule name cannot exceed 200 characters.");

        RuleFor(x => x.MaxHoursPerPeriod)
            .InclusiveBetween(1, 168).WithMessage("Max hours per period must be between 1 and 168.");

        RuleFor(x => x.MinHoursBetweenShifts)
            .InclusiveBetween(0, 48).WithMessage("Min hours between shifts must be between 0 and 48.");

        RuleFor(x => x.MaxShiftLengthHours)
            .InclusiveBetween(1, 36).WithMessage("Max shift length must be between 1 and 36 hours.");

        RuleFor(x => x.MaxConsecutiveDays)
            .InclusiveBetween(1, 30).WithMessage("Max consecutive days must be between 1 and 30.");
    }
}

public class AppSettingValidator : AbstractValidator<AppSetting>
{
    public AppSettingValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Setting key is required.")
            .MaximumLength(100).WithMessage("Setting key cannot exceed 100 characters.");

        RuleFor(x => x.Value)
            .NotEmpty().WithMessage("Setting value is required.");
    }
}

public class PhoneTreeValidator : AbstractValidator<PhoneTree>
{
    public PhoneTreeValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Phone tree name is required.")
            .MaximumLength(200).WithMessage("Phone tree name cannot exceed 200 characters.");

        RuleFor(x => x.TreeType)
            .NotEmpty().WithMessage("Tree type is required.")
            .Must(t => new[] { "emergency", "department", "oncall", "admin" }.Contains(t))
            .WithMessage("Tree type must be one of: emergency, department, oncall, admin.");
    }
}

public class PhoneTreeNodeValidator : AbstractValidator<PhoneTreeNode>
{
    public PhoneTreeNodeValidator()
    {
        RuleFor(x => x.Order)
            .GreaterThan(0).WithMessage("Node order must be a positive number.");

        RuleFor(x => x.TimeoutSeconds)
            .InclusiveBetween(0, 600).WithMessage("Timeout must be between 0 and 600 seconds.");

        // At least one of EmployeeId or RoleName must be set
        RuleFor(x => x)
            .Must(node => !string.IsNullOrEmpty(node.EmployeeId?.ToString()) || !string.IsNullOrEmpty(node.RoleName))
            .WithMessage("Either an employee or a role name is required for each node.");
    }
}
