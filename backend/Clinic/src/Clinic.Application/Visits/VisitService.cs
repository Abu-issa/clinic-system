using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Visits;

// Staff authorization belongs at the future API boundary. Actor must come from its trusted principal.
// Every existing-visit operation includes PatientId to bind lookup to the authorized resource.
public sealed class VisitService(IVisitStore store, TimeProvider clock, IAuditMutationWriter auditWriter)
{
    public async Task<VisitResult> CreateAsync(CreateVisitRequest request, string actor, CancellationToken ct = default)
    {
        try
        {
            var visit = new Visit(request.PatientId, request.DoctorId, request.AppointmentId, request.OccurredAtUtc, actor, clock.GetUtcNow());
            if (!await store.PatientExistsAsync(request.PatientId, ct)) return Fail(VisitError.PatientNotFound);
            if (!await store.DoctorExistsAsync(request.DoctorId, ct)) return Fail(VisitError.DoctorNotFound);
            if (request.AppointmentId is { } appointmentId)
            {
                var appointment = await store.AppointmentAsync(appointmentId, ct);
                if (appointment is null) return Fail(VisitError.AppointmentNotFound);
                if (appointment.PatientId != request.PatientId || appointment.DoctorId != request.DoctorId)
                    return Fail(VisitError.AppointmentMismatch);
            }
            store.Add(visit); await store.SaveAsync(ct); return VisitResult.Success(Map(visit));
        }
        catch (ArgumentException) { return Fail(VisitError.InvalidInput); }
        catch { store.DiscardChanges(); throw; }
    }

    public async Task<VisitResult> GetAsync(Guid patientId, Guid visitId, CancellationToken ct = default)
    {
        var visit = await store.GetAsync(patientId, visitId, ct);
        return visit is null ? VisitResult.Failure(VisitError.VisitNotFound) : VisitResult.Success(Map(visit));
    }

    public async Task<VisitListResult> ListAsync(Guid patientId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        if (skip < 0 || take is < 1 or > 100) return new(false, VisitError.InvalidInput, []);
        if (!await store.PatientExistsAsync(patientId, ct)) return new(false, VisitError.PatientNotFound, []);
        return new(true, VisitError.None, await store.ListAsync(patientId, skip, take, ct));
    }

    public Task<VisitResult> UpdateAsync(Guid patientId, Guid visitId, UpdateVisitRequest request, string actor, CancellationToken ct = default) =>
        Mutate(patientId, visitId, request.ExpectedRowVersion, v => v.UpdateDraft(request.Content, actor, clock.GetUtcNow()), ct);
    public Task<VisitResult> AddVitalAsync(Guid patientId, Guid visitId, AddVitalRequest request, string actor, CancellationToken ct = default) =>
        Mutate(patientId, visitId, request.ExpectedRowVersion, v => v.AddVital(request.Reading, request.MeasuredAtUtc, actor, clock.GetUtcNow()), ct);
    public Task<VisitResult> FinalizeAsync(Guid patientId, Guid visitId, byte[] expectedRowVersion, string actor, CancellationToken ct = default) =>
        Mutate(patientId, visitId, expectedRowVersion, v => v.FinalizeVisit(actor, clock.GetUtcNow()), ct,
            audit: v => new AuditAppendRequest(actor, "visit.finalize", "visit",
                v.Id.ToString("N"), v.PatientId, AuditOutcome.Succeeded, null, null));
    public Task<VisitResult> AddAmendmentAsync(Guid patientId, Guid visitId, AddAmendmentRequest request, string actor, CancellationToken ct = default) =>
        Mutate(patientId, visitId, request.ExpectedRowVersion, v => v.AddAmendment(request.Reason, request.AmendmentText, actor, clock.GetUtcNow()), ct,
            audit: v => new AuditAppendRequest(actor, "visit.amend", "visit",
                v.Id.ToString("N"), v.PatientId, AuditOutcome.Succeeded, null, null));

    private async Task<VisitResult> Mutate(Guid patientId, Guid visitId, byte[] version, Action<Visit> mutation, CancellationToken ct,
        Func<Visit, AuditAppendRequest>? audit = null)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (version is not { Length: 8 }) return Fail(VisitError.InvalidRowVersion);
        try
        {
            var visit = await store.GetAsync(patientId, visitId, ct);
            if (visit is null) return Fail(VisitError.VisitNotFound);
            if (!visit.RowVersion.SequenceEqual(version)) return Fail(VisitError.VisitChanged);
            try { mutation(visit); }
            catch (ArgumentException) { return Fail(VisitError.InvalidInput); }
            catch (InvalidOperationException) { return Fail(VisitError.InvalidLifecycle); }
            store.ExpectVersion(visit, version);
            if (audit is not null) auditWriter.Append(audit(visit));
            await store.SaveAsync(ct);
            return VisitResult.Success(Map(visit));
        }
        catch (PersistenceConcurrencyException) { return Fail(VisitError.VisitChanged); }
        catch { store.DiscardChanges(); throw; }
    }
    private VisitResult Fail(VisitError error) { store.DiscardChanges(); return VisitResult.Failure(error); }
    private static VisitDetails Map(Visit v) => new(v.Id, v.PatientId, v.DoctorId, v.AppointmentId, v.OccurredAtUtc, v.Status,
        v.ChiefComplaint, v.Symptoms, v.Diagnosis, v.ClinicianNotes, v.InternalNotes, v.PatientSummary, v.SuggestedFollowUpAtUtc,
        v.CreatedAtUtc, v.CreatedByStaffId, v.LastModifiedAtUtc, v.LastModifiedByStaffId, v.FinalizedAtUtc, v.FinalizedByStaffId,
        v.RowVersion.ToArray(), v.Amendments.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new AmendmentDetails(x.Id, x.Reason, x.AmendmentText, x.CreatedAtUtc, x.CreatedByStaffId)).ToArray(),
        v.VitalMeasurements.OrderBy(x => x.MeasuredAtUtc).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new VitalDetails(x.Id, x.Type, x.Unit, x.SystolicMmHg, x.DiastolicMmHg, x.HeartRateBpm,
                x.TemperatureCelsius, x.OxygenSaturationPercent, x.WeightKg, x.HeightCm, x.RespiratoryRateBreathsPerMin,
                x.MeasuredAtUtc, x.CreatedAtUtc, x.CreatedByStaffId)).ToArray());
}
