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

    /// <summary>
    /// Minimum digits, country code included, for a number anyone could actually dial.
    /// The shortest real mobile numbers (Denmark, Iceland) reach ten this way, while an
    /// internal extension does not.
    /// </summary>
    private const int MinimumDialableDigits = 10;

    /// <summary>
    /// Normalizes to E.164 and rejects anything too short to be a real destination.
    ///
    /// <see cref="NormalizeToE164"/> alone is deliberately best-effort for directory
    /// display, so it happily promotes the extension "4412" to "+14412" and the local
    /// fragment "555-1234" to "+15551234". Sending a code-call alert to either goes
    /// nowhere silently, so anywhere a number must be dialable uses this instead.
    /// </summary>
    public static string? NormalizeToDialable(string? phone, string defaultCountryCode = "1")
    {
        // A number carrying an extension is refused rather than repaired. Stripping
        // non-digits merges the extension into the number itself -- "202-555-0134 x4412"
        // became "+120255501344412", which is 15 digits, passes the E.164 regex and clears
        // the dialable floor, so it looked entirely valid and would have been dialled.
        // Dropping the extension instead would silently reroute a page to a switchboard.
        // Refusing puts the problem in front of whoever is importing the data.
        if (HasExtension(phone)) return null;

        var normalized = NormalizeToE164(phone, defaultCountryCode);
        if (normalized == null) return null;

        return normalized.Count(char.IsDigit) >= MinimumDialableDigits ? normalized : null;
    }

    /// <summary>Markers real HR exports use for an extension, e.g. "555-0134 x4412".</summary>
    private static bool HasExtension(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;

        var value = phone.Trim();
        // A leading marker is a bare extension, which the dialable floor already rejects;
        // what matters here is a marker following an otherwise plausible number.
        var firstDigit = value.IndexOfAny("0123456789".ToCharArray());
        if (firstDigit < 0) return false;

        var tail = value[firstDigit..];
        return ExtensionMarker.IsMatch(tail);
    }

    private static readonly Regex ExtensionMarker =
        new(@"(?:\bx|\bext\.?|\bextension\b|#)\s*\d+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Separates "202-555-0134 x4412" into the number and the extension, so each can be
    /// stored in the field that means it.
    ///
    /// <see cref="NormalizeToDialable"/> refuses such a value outright and is right to:
    /// merging the two produced "+120255501344412", which passes E.164, clears the
    /// dialable floor and would have been dialled. That refusal stays exactly as it is.
    /// What was missing was a way to keep BOTH halves instead of losing the row, which is
    /// what a directory full of desk lines actually needs.
    ///
    /// A bare extension ("x3434", "3434") yields a null number and a set extension; the
    /// caller decides whether it has a dial plan to build a real number from. Nothing here
    /// invents one.
    /// </summary>
    /// <returns>
    /// The number with the extension removed (null when there was nothing but an
    /// extension), and the extension digits (null when there was no extension).
    /// </returns>
    public static (string? Number, string? Extension) SplitExtension(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return (null, null);

        var value = phone.Trim();

        var marker = ExtensionMarker.Match(value);
        if (marker.Success)
        {
            var head = value[..marker.Index].Trim().TrimEnd(',', ';', '-', '.', '/').Trim();
            var digits = new string(marker.Value.Where(char.IsDigit).ToArray());

            return (string.IsNullOrWhiteSpace(head) ? null : head,
                    digits.Length == 0 ? null : digits);
        }

        // No marker. A short all-digit value is a bare extension: too short to dial, and
        // storing it as a number would put something undialable in the phone column.
        var bare = new string(value.Where(char.IsDigit).ToArray());
        if (bare.Length > 0
            && bare.Length == value.Count(c => !char.IsWhiteSpace(c))
            && bare.Length <= MaxBareExtensionDigits)
        {
            return (null, bare);
        }

        return (value, null);
    }

    /// <summary>
    /// The longest all-digit value read as an extension rather than a phone number.
    /// Deliberately below <see cref="MinimumDialableDigits"/>, so nothing that could be a
    /// real number is ever demoted to an extension and quietly stops being dialled.
    /// </summary>
    private const int MaxBareExtensionDigits = 7;

    /// <summary>
    /// Builds the full external number for an extension, given a tenant's dial-plan prefix
    /// (prefix "845568" + extension "3434" gives "+18455683434").
    ///
    /// Returns null when there is no prefix, when the extension holds no digits, or when
    /// the result is not dialable. Never guesses: an outside caller dialling a fabricated
    /// number reaches a stranger's switchboard, which is worse than the directory
    /// admitting it only knows the internal extension.
    /// </summary>
    public static string? BuildNumberFromExtension(string? prefix, string? extension,
        string defaultCountryCode = "1")
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(extension))
            return null;

        var prefixDigits = new string(prefix.Where(char.IsDigit).ToArray());
        var extensionDigits = new string(extension.Where(char.IsDigit).ToArray());
        if (prefixDigits.Length == 0 || extensionDigits.Length == 0) return null;

        // A prefix that already ends with the extension means the whole number was given
        // as the prefix; concatenating would repeat the last digits.
        var combined = prefixDigits.EndsWith(extensionDigits, StringComparison.Ordinal)
            ? prefixDigits
            : prefixDigits + extensionDigits;

        return NormalizeToDialable(combined, defaultCountryCode);
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
        // A person still needs a name and an address. A "Department" contact is a unit
        // reached by phone -- "3North", x3434 -- and has neither, so requiring them made
        // such a contact impossible to store. What it must have instead is a label and
        // some way to reach it, asserted below.
        RuleFor(x => x.FirstName).NotEmpty().When(IsPerson).MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().When(IsPerson).MaximumLength(100);
        RuleFor(x => x.Email).NotEmpty().When(IsPerson);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.ContactType)
            .Must(ContactType.IsKnown)
            .WithMessage("ContactType must be either 'Person' or 'Department'.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().When(x => !IsPerson(x))
            .WithMessage("A department contact needs a name to show, e.g. '3North'.")
            .MaximumLength(200);

        // A department contact that reaches nobody is not worth storing: it looks like a
        // route in the directory and is a dead end when someone dials it.
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.OfficePhone)
                       || !string.IsNullOrWhiteSpace(x.MobilePhone)
                       || !string.IsNullOrWhiteSpace(x.PagerNumber)
                       || !string.IsNullOrWhiteSpace(x.Extension))
            .When(x => !IsPerson(x))
            .WithMessage("A department contact needs a phone number or an extension.");

        RuleFor(x => x.Extension)
            .Matches(@"^\d{1,16}$")
            .WithMessage("Extension must be digits only, e.g. 3434.")
            .When(x => !string.IsNullOrWhiteSpace(x.Extension));
        RuleFor(x => x.OfficePhone).E164Phone("Office phone");
        RuleFor(x => x.MobilePhone).E164Phone("Mobile phone");
        RuleFor(x => x.PagerNumber).E164Phone("Pager number");
        // AzureAdObjectId can be auto-generated or provided; both are valid
    }

    /// <summary>
    /// Anything that is not explicitly a department contact is treated as a person, so an
    /// unset or unrecognised ContactType keeps the STRICTER rules rather than falling
    /// through to the laxer ones.
    /// </summary>
    private static bool IsPerson(Employee employee) =>
        employee.ContactType != ContactType.Department;
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
