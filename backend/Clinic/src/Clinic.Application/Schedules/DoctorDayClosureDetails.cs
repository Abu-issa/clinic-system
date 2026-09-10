using Clinic.Domain.Enums;

namespace Clinic.Application.Schedules;

public sealed record DoctorDayClosureDetails(
    Guid ClosureId,
    Guid DoctorId,
    DateOnly LocalDate,
    string Reason,
    IReadOnlyList<ClosureAffectedAppointment> AffectedAppointments);

public sealed record ClosureAffectedAppointment(
    Guid AppointmentId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    AppointmentStatus Status);
