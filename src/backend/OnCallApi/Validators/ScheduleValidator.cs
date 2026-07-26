using FluentValidation;
using OnCallApi.Models;

namespace OnCallApi.Validators;

public class ScheduleValidator : AbstractValidator<Schedule>
{
    public ScheduleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.RotationType)
            .NotEmpty()
            .Must(r => r is "weekly" or "biweekly" or "monthly")
            .WithMessage("Rotation type must be weekly, biweekly, or monthly.");
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate)
            .WithMessage("End date must be after start date.");
    }
}

public class ShiftValidator : AbstractValidator<Shift>
{
    public ShiftValidator()
    {
        RuleFor(x => x.Tier)
            .NotEmpty()
            .Must(t => t is "primary" or "secondary" or "tertiary")
            .WithMessage("Tier must be primary, secondary, or tertiary.");
        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(s => s is "scheduled" or "swapped" or "covered" or "gap")
            .WithMessage("Status must be scheduled, swapped, covered, or gap.");
        RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime)
            .WithMessage("End time must be after start time.");
    }
}

public class TimeOffValidator : AbstractValidator<TimeOff>
{
    public TimeOffValidator()
    {
        RuleFor(x => x.Type)
            .NotEmpty()
            .Must(t => t is "pto" or "cme" or "holiday" or "sick" or "personal" or "bereavement" or "military" or "jury_duty" or "unpaid")
            .WithMessage("Type must be pto, cme, holiday, sick, personal, bereavement, military, jury_duty, or unpaid.");
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate)
            .WithMessage("End date must be on or after start date.");
    }
}

public class ShiftSwapValidator : AbstractValidator<ShiftSwap>
{
    public ShiftSwapValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(s => s is "pending" or "approved" or "rejected" or "cancelled")
            .WithMessage("Status must be pending, approved, rejected, or cancelled.");
    }
}

public class AssignShiftRequestValidator : AbstractValidator<AssignShiftRequest>
{
    public AssignShiftRequestValidator()
    {
        RuleFor(x => x.EmployeeId)
            .NotEmpty().WithMessage("Employee ID is required.");

        RuleFor(x => x.StartTime)
            .NotEmpty().WithMessage("Start time is required.");

        RuleFor(x => x.EndTime)
            .NotEmpty().WithMessage("End time is required.")
            .GreaterThan(x => x.StartTime).WithMessage("End time must be after start time.");

        RuleFor(x => x.Tier)
            .NotEmpty()
            .Must(t => t is "primary" or "secondary" or "tertiary")
            .WithMessage("Tier must be primary, secondary, or tertiary.");
    }
}

public class SwapRequestValidator : AbstractValidator<SwapRequest>
{
    public SwapRequestValidator()
    {
        RuleFor(x => x.ShiftId)
            .GreaterThan(0).WithMessage("Shift ID is required.");

        RuleFor(x => x.RequestedById)
            .NotEmpty().WithMessage("Requester ID is required.");

        RuleFor(x => x.Reason)
            .MaximumLength(500).WithMessage("Reason cannot exceed 500 characters.");
    }
}
