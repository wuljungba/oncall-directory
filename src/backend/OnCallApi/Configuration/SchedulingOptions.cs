namespace OnCallApi.Configuration;

/// <summary>
/// How generated rotations map to the wall clock people actually work to.
///
/// Shifts were previously built from <c>DateTime.UtcNow.Date</c> plus an hour offset, so a
/// "7a-7p" rotation was created at 07:00 UTC — the middle of the night for a US hospital.
/// Everything downstream (who is on call now, which shift a code call resolves to) then
/// inherited that error.
/// </summary>
public class SchedulingOptions
{
    public const string SectionName = "Scheduling";

    /// <summary>
    /// The hospital's local time zone, as an IANA id ("America/New_York") or a Windows id
    /// ("Eastern Standard Time"). .NET 8 accepts either on both platforms.
    ///
    /// Stored times remain UTC; this only decides what "7am" means when a rotation is
    /// generated, and it follows daylight saving automatically.
    /// </summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>Local hour the day shift begins. Default 07:00.</summary>
    public int DayShiftStartHour { get; set; } = 7;

    /// <summary>Local hour the night shift begins. Default 19:00.</summary>
    public int NightShiftStartHour { get; set; } = 19;
}
