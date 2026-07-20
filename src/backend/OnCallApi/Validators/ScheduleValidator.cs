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
            .Must(t => t is "pto" or "cme" or "holiday" or "sick")
            .WithMessage("Type must be pto, cme, holiday, or sick.");
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
