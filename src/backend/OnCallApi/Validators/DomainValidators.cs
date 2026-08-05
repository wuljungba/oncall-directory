using System.Text.RegularExpressions;
using FluentValidation;
using OnCallApi.Models;

namespace OnCallApi.Validators;

/// <summary>Reusable E.164 phone number validation.</summary>
public static partial class PhoneValidation
{
    private static readonly Regex E164Regex = MyRegex();

    [GeneratedRegex(@"^\+[1-9]\d{1,14}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    public static bool IsValidE164(string? phone) =>
        phone == null || E164Regex.IsMatch(phone);

    /// <summary>
    /// Best-effort normalization of a phone number to E.164 for directory data
    /// (e.g. numbers synced from AD/Graph, which are not always stored in canonical
    /// E.164). Strips non-digit formatting — "202 555 0134", "(202) 555-0134",
    /// "+1 202-555-0134" — and, when no explicit country code is present, prepends
    /// a default country code. Returns null for null/blank/unsalvageable input.
    /// </summary>
    public static string? NormalizeToE164(string? phone, string defaultCountryCode = "1")
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var raw = phone.Trim();
        var hasPlus = raw.StartsWith('+');
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        // A leading '+' is taken as "the number already carries its country code".
        var normalized = hasPlus ? digits : defaultCountryCode + digits;

        // E.164 upper bound is 15 digits including the country code.
        if (normalized.Length > 15) normalized = normalized[..15];
        if (normalized.Length < 2) return null;

        var candidate = "+" + normalized;
        return E164Regex.IsMatch(candidate) ? candidate : null;
    }

    public static IRuleBuilderOptions<T, string?> E164Phone<T>(this IRuleBuilder<T, string?> ruleBuilder, string fieldName)
    {
        return ruleBuilder.Must(IsValidE164)
            .WithMessage($"{fieldName} must be in E.164 format (+ followed by 2-15 digits, e.g. +1234567890).")
            .When(x => !string.IsNullOrEmpty(x as string));
    }
}

public class EmployeeValidator : AbstractValidator<Employee>
{
    public EmployeeValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.OfficePhone).E164Phone("Office phone");
        RuleFor(x => x.MobilePhone).E164Phone("Mobile phone");
        RuleFor(x => x.PagerNumber).E164Phone("Pager number");
        // AzureAdObjectId can be auto-generated or provided; both are valid
    }
}

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
            .Must(t => new[] { "emergency", "department", "oncall", "admin", "code-blue", "code-red", "code-green", "code-silver", "code-grey", "code-pink" }.Contains(t))
            .WithMessage("Tree type must be a valid emergency code or general type.");
    }
}

public class PhoneTreeEventValidator : AbstractValidator<PhoneTreeEvent>
{
    public PhoneTreeEventValidator()
    {
        RuleFor(x => x.PhoneTreeId)
            .GreaterThan(0).WithMessage("Phone tree ID is required.");

        RuleFor(x => x.StartedAt)
            .NotEmpty().WithMessage("Start time is required.");

        RuleFor(x => x.EndedAt)
            .GreaterThanOrEqualTo(x => x.StartedAt)
            .When(x => x.EndedAt.HasValue)
            .WithMessage("End time must be on or after start time.");

        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(s => s is "active" or "completed")
            .WithMessage("Status must be active or completed.");

        RuleFor(x => x.Location)
            .MaximumLength(200).WithMessage("Location cannot exceed 200 characters.");

        RuleFor(x => x.LocationZone)
            .MaximumLength(100).WithMessage("Location zone cannot exceed 100 characters.");

        RuleFor(x => x.ExternalIncidentId)
            .MaximumLength(100).WithMessage("External incident ID cannot exceed 100 characters.");

        RuleFor(x => x.Outcome)
            .MaximumLength(1000).WithMessage("Outcome cannot exceed 1000 characters.");

        RuleFor(x => x.Notes)
            .MaximumLength(1000).WithMessage("Notes cannot exceed 1000 characters.");
    }
}

public class PhoneTreeEventParticipantValidator : AbstractValidator<PhoneTreeEventParticipant>
{
    public PhoneTreeEventParticipantValidator()
    {
        RuleFor(x => x.Role)
            .MaximumLength(50).WithMessage("Role cannot exceed 50 characters.");

        RuleFor(x => x.Notes)
            .MaximumLength(500).WithMessage("Notes cannot exceed 500 characters.");
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
