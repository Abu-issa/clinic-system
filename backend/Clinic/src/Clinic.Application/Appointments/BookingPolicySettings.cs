namespace Clinic.Application.Appointments;

// Provisional workflow assumptions, not clinician-approved production settings.
public sealed record BookingPolicySettings
{
    public int ConsultationMinutes { get; init; } = 30;
    public int FollowUpMinutes { get; init; } = 15;
    public int SlotStartIntervalMinutes { get; init; } = 15;
    public int MinimumAdvanceNoticeMinutes { get; init; } = 60;
    public int BookingHorizonDays { get; init; } = 30;

    public bool IsValid() =>
        ConsultationMinutes is > 0 and <= 1440 &&
        FollowUpMinutes is > 0 and <= 1440 &&
        SlotStartIntervalMinutes is > 0 and <= 1440 &&
        MinimumAdvanceNoticeMinutes is >= 0 and <= 525600 &&
        BookingHorizonDays is >= 0 and <= 366;
}
