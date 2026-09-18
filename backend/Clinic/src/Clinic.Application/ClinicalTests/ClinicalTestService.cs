using Clinic.Application.Audit;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.ClinicalTests;

// API enforces Doctor + MFA + permission + patient scope. Actor is supplied only by the
// trusted principal; doctor association and visit ownership are re-read here from persistence.
public sealed class ClinicalTestService(IClinicalTestStore store, IAuditMutationWriter audit, TimeProvider clock)
{
    // Minimal patient identity is disclosed only inside an authorized, audited test page.
    public Task<ClinicalTestPatientContext?> PatientContextAsync(Guid patientId, CancellationToken ct) => store.PatientContextAsync(patientId, ct);

    public async Task<IReadOnlyList<ClinicalTestVisitChoice>> VisitChoicesAsync(Guid patientId, string actor, CancellationToken ct)
    {
        var doctor = await store.AssociatedDoctorAsync(actor, ct);
        return doctor is null || doctor == Guid.Empty ? [] : await store.VisitChoicesAsync(patientId, doctor.Value, ct);
    }
    public async Task<ClinicalTestResult> CreateAsync(CreateClinicalTestRequest input, string actor, CancellationToken ct = default)
    {
        if (input.PatientId == Guid.Empty || input.VisitId == Guid.Empty || !Enum.IsDefined(input.Category) || string.IsNullOrWhiteSpace(actor))
            return new(ClinicalTestError.InvalidInput);
        if (!await store.PatientExistsAsync(input.PatientId, ct)) return new(ClinicalTestError.PatientNotFound);
        Guid? visitDoctor = null;
        if (input.VisitId is { } visitId)
        {
            visitDoctor = await store.VisitDoctorAsync(input.PatientId, visitId, ct);
            if (visitDoctor is null) return new(ClinicalTestError.VisitNotFound);
        }
        var doctor = await store.AssociatedDoctorAsync(actor, ct);
        if (doctor is null || doctor == Guid.Empty || (visitDoctor is not null && doctor != visitDoctor))
            return new(ClinicalTestError.DoctorAuthority);
        ClinicalTestRequest request;
        try
        {
            request = new(input.PatientId, input.VisitId, doctor.Value, input.Category,
                input.TestName, input.ClinicalInstructions, clock.GetUtcNow());
        }
        catch (ArgumentException) { return new(ClinicalTestError.InvalidInput); }
        using var auditScope = audit.BeginMutation();
        try
        {
            store.Add(request);
            // Empty metadata by design: no test names, instructions or other clinical prose.
            audit.Append(new(actor, "test-request.create", "test-request", request.Id.ToString("N"),
                request.PatientId, AuditOutcome.Succeeded, null, null));
            await store.SaveAsync(ct);
            return new(ClinicalTestError.None, Map(request));
        }
        catch { store.DiscardChanges(); throw; }
    }

    public async Task<ClinicalTestResult> GetAsync(Guid patientId, Guid requestId, CancellationToken ct = default)
    {
        if (patientId == Guid.Empty || requestId == Guid.Empty) return new(ClinicalTestError.InvalidInput);
        var details = await store.GetAsync(patientId, requestId, ct);
        return details is null ? new(ClinicalTestError.RequestNotFound) : new(ClinicalTestError.None, details);
    }

    public async Task<ClinicalTestListResult> ListAsync(Guid patientId, ClinicalTestCategory? category,
        ClinicalTestStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        if (patientId == Guid.Empty || page is < 1 or > 1000 || pageSize is < 1 or > 100 ||
            (category.HasValue && !Enum.IsDefined(category.Value)) || (status.HasValue && !Enum.IsDefined(status.Value)))
            return new(ClinicalTestError.InvalidInput, []);
        if (!await store.PatientExistsAsync(patientId, ct)) return new(ClinicalTestError.PatientNotFound, []);
        return new(ClinicalTestError.None, await store.ListAsync(patientId, category, status, page, pageSize, ct));
    }

    private static ClinicalTestDetails Map(ClinicalTestRequest x) => new(x.Id, x.PatientId, x.VisitId,
        x.Category, x.TestName, x.ClinicalInstructions, x.Status, x.RequestedAtUtc);
}
