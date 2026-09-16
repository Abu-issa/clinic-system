using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Prescriptions;

/// <summary>
/// Application service for Prescription management.
/// Prescriptions derive PatientId and DoctorId strictly from their parent Visit.
/// Lifecycle: Draft -> Finalized -> Released -> Cancelled.
/// Concurrency is protected via SQL RowVersion. Changes are transactional and safely detached on error.
/// Successful mutations append an AuditEvent into the same unit of work (atomic with the mutation).
/// </summary>
public sealed class PrescriptionService(IPrescriptionStore store, TimeProvider clock, IAuditMutationWriter auditWriter)
{
    public async Task<PrescriptionResult> CreateDraftAsync(
        CreatePrescriptionDraftRequest request,
        string actor,
        CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (string.IsNullOrWhiteSpace(actor))
            return Fail(PrescriptionError.InvalidInput);
        if (request.VisitId == Guid.Empty)
            return Fail(PrescriptionError.InvalidInput);

        try
        {
            var visitRef = await store.GetVisitReferenceAsync(request.VisitId, ct);
            if (visitRef is null)
                return Fail(PrescriptionError.VisitNotFound);

            var now = clock.GetUtcNow();
            Prescription? originalToReplace = null;

            if (request.ReplacesPrescriptionId is { } replacesId)
            {
                // Creating a replacement also mutates the cancelled original, so the caller must
                // hold the original's current rowversion: concurrent replacement attempts are
                // serialized by the original's optimistic-concurrency update.
                if (request.ExpectedOriginalRowVersion is not { Length: 8 })
                    return Fail(PrescriptionError.InvalidRowVersion);

                originalToReplace = await store.GetAsync(visitRef.PatientId, replacesId, ct);
                if (originalToReplace is null)
                    return Fail(PrescriptionError.PrescriptionNotFound);

                // Replacement validation: must be cancelled, must belong to same visit, patient, doctor
                if (originalToReplace.Status != PrescriptionStatus.Cancelled ||
                    originalToReplace.VisitId != request.VisitId ||
                    originalToReplace.PatientId != visitRef.PatientId ||
                    originalToReplace.DoctorId != visitRef.DoctorId ||
                    originalToReplace.ReplacedByPrescriptionId != null)
                {
                    return Fail(PrescriptionError.ReplacementMismatch);
                }
                if (!originalToReplace.RowVersion.SequenceEqual(request.ExpectedOriginalRowVersion))
                    return Fail(PrescriptionError.PrescriptionChanged);
            }

            var prescription = new Prescription(
                request.VisitId,
                visitRef.PatientId,
                visitRef.DoctorId,
                request.Notes,
                request.ReplacesPrescriptionId,
                actor,
                now);

            store.Add(prescription);

            if (originalToReplace != null)
            {
                // Both replacement directions are written in this single save: the new
                // prescription's ReplacesPrescriptionId and the original's ReplacedByPrescriptionId.
                originalToReplace.SetReplacedBy(prescription.Id, actor, now);
                store.ExpectVersion(originalToReplace, request.ExpectedOriginalRowVersion!);
            }

            // One audit event for the semantic operation: a plain draft or a replacement draft.
            if (originalToReplace != null)
                auditWriter.Append(new AuditAppendRequest(actor, "prescription.replace", "prescription",
                    prescription.Id.ToString("N"), prescription.PatientId, AuditOutcome.Succeeded, null,
                    [new KeyValuePair<string, string>("replaces-prescription", originalToReplace.Id.ToString("N"))]));
            else
                auditWriter.Append(new AuditAppendRequest(actor, "prescription.create", "prescription",
                    prescription.Id.ToString("N"), prescription.PatientId, AuditOutcome.Succeeded, null, null));

            await store.SaveAsync(ct);
            return PrescriptionResult.Success(Map(prescription));
        }
        catch (ArgumentException)
        {
            return Fail(PrescriptionError.InvalidInput);
        }
        catch (InvalidOperationException)
        {
            return Fail(PrescriptionError.ReplacementMismatch);
        }
        catch (PersistenceConcurrencyException)
        {
            // The original lost the replacement race: another replacement already claimed it.
            return Fail(PrescriptionError.PrescriptionChanged);
        }
        catch (PrescriptionReplacementConflictException)
        {
            // Database-level backstop: the original already has a replacement (or the replacement
            // link was claimed) despite the rowversion checks. No identifiers are leaked.
            return Fail(PrescriptionError.ReplacementMismatch);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public async Task<PrescriptionResult> GetAsync(Guid patientId, Guid prescriptionId, CancellationToken ct = default)
    {
        var prescription = await store.GetAsync(patientId, prescriptionId, ct);
        return prescription is null
            ? PrescriptionResult.Failure(PrescriptionError.PrescriptionNotFound)
            : PrescriptionResult.Success(Map(prescription));
    }

    public async Task<PrescriptionListResult> ListByVisitAsync(Guid patientId, Guid visitId, CancellationToken ct = default)
    {
        if (patientId == Guid.Empty || visitId == Guid.Empty)
            return new(false, PrescriptionError.InvalidInput, []);

        try
        {
            var items = await store.ListByVisitAsync(patientId, visitId, ct);
            return new(true, PrescriptionError.None, items);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public async Task<PrescriptionListResult> ListByPatientAsync(Guid patientId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        if (patientId == Guid.Empty || skip < 0 || take is < 1 or > 100)
            return new(false, PrescriptionError.InvalidInput, []);

        try
        {
            var items = await store.ListByPatientAsync(patientId, skip, take, ct);
            return new(true, PrescriptionError.None, items);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public Task<PrescriptionResult> UpdateDraftNotesAsync(
        Guid patientId,
        Guid prescriptionId,
        UpdatePrescriptionNotesRequest request,
        string actor,
        CancellationToken ct = default) =>
        Mutate(patientId, prescriptionId, request.ExpectedRowVersion, p => p.UpdateDraftNotes(request.Notes, actor, clock.GetUtcNow()), ct,
            audit: p => new AuditAppendRequest(actor, "prescription.notes.update", "prescription",
                p.Id.ToString("N"), p.PatientId, AuditOutcome.Succeeded, null, null));

    public async Task<PrescriptionResult> AddItemAsync(
        Guid patientId,
        Guid prescriptionId,
        AddPrescriptionItemRequest request,
        string actor,
        CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (request.ExpectedRowVersion is not { Length: 8 })
            return Fail(PrescriptionError.InvalidRowVersion);

        try
        {
            var prescription = await store.GetAsync(patientId, prescriptionId, ct);
            if (prescription is null)
                return Fail(PrescriptionError.PrescriptionNotFound);
            if (!prescription.RowVersion.SequenceEqual(request.ExpectedRowVersion))
                return Fail(PrescriptionError.PrescriptionChanged);
            if (prescription.Status != PrescriptionStatus.Draft)
                return Fail(PrescriptionError.InvalidLifecycle);

            var medication = await store.GetMedicationAsync(request.MedicationId, ct);
            if (medication is null)
                return Fail(PrescriptionError.MedicationNotFound);
            if (!medication.IsActive)
                return Fail(PrescriptionError.InactiveMedication);

            try
            {
                prescription.AddItem(
                    medication,
                    request.Dose,
                    request.Frequency,
                    request.Duration,
                    request.Instructions,
                    request.DisplayOrder,
                    actor,
                    clock.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return Fail(PrescriptionError.InvalidInput);
            }
            catch (InvalidOperationException)
            {
                return Fail(PrescriptionError.InvalidLifecycle);
            }

            store.ExpectVersion(prescription, request.ExpectedRowVersion);
            auditWriter.Append(new AuditAppendRequest(actor, "prescription.item.add", "prescription",
                prescription.Id.ToString("N"), prescription.PatientId, AuditOutcome.Succeeded, null,
                [new KeyValuePair<string, string>("item.count", prescription.Items.Count.ToString())]));
            await store.SaveAsync(ct);
            return PrescriptionResult.Success(Map(prescription));
        }
        catch (PersistenceConcurrencyException)
        {
            return Fail(PrescriptionError.PrescriptionChanged);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public async Task<PrescriptionResult> UpdateItemAsync(
        Guid patientId,
        Guid prescriptionId,
        Guid itemId,
        UpdatePrescriptionItemRequest request,
        string actor,
        CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (request.ExpectedRowVersion is not { Length: 8 })
            return Fail(PrescriptionError.InvalidRowVersion);

        try
        {
            var prescription = await store.GetAsync(patientId, prescriptionId, ct);
            if (prescription is null)
                return Fail(PrescriptionError.PrescriptionNotFound);
            if (!prescription.RowVersion.SequenceEqual(request.ExpectedRowVersion))
                return Fail(PrescriptionError.PrescriptionChanged);
            if (prescription.Status != PrescriptionStatus.Draft)
                return Fail(PrescriptionError.InvalidLifecycle);

            Medication? reselectedMedication = null;
            if (request.ReselectedMedicationId is { } medId)
            {
                reselectedMedication = await store.GetMedicationAsync(medId, ct);
                if (reselectedMedication is null)
                    return Fail(PrescriptionError.MedicationNotFound);
                if (!reselectedMedication.IsActive)
                    return Fail(PrescriptionError.InactiveMedication);
            }

            try
            {
                prescription.UpdateItem(
                    itemId,
                    reselectedMedication,
                    request.Dose,
                    request.Frequency,
                    request.Duration,
                    request.Instructions,
                    request.DisplayOrder,
                    actor,
                    clock.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return Fail(PrescriptionError.InvalidInput);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return Fail(PrescriptionError.PrescriptionItemNotFound);
            }
            catch (InvalidOperationException)
            {
                return Fail(PrescriptionError.InvalidLifecycle);
            }

            store.ExpectVersion(prescription, request.ExpectedRowVersion);
            auditWriter.Append(new AuditAppendRequest(actor, "prescription.item.update", "prescription",
                prescription.Id.ToString("N"), prescription.PatientId, AuditOutcome.Succeeded, null,
                [new KeyValuePair<string, string>("item.count", prescription.Items.Count.ToString())]));
            await store.SaveAsync(ct);
            return PrescriptionResult.Success(Map(prescription));
        }
        catch (PersistenceConcurrencyException)
        {
            return Fail(PrescriptionError.PrescriptionChanged);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public async Task<PrescriptionResult> RemoveItemAsync(
        Guid patientId,
        Guid prescriptionId,
        Guid itemId,
        byte[] expectedRowVersion,
        string actor,
        CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (expectedRowVersion is not { Length: 8 })
            return Fail(PrescriptionError.InvalidRowVersion);

        try
        {
            var prescription = await store.GetAsync(patientId, prescriptionId, ct);
            if (prescription is null)
                return Fail(PrescriptionError.PrescriptionNotFound);
            if (!prescription.RowVersion.SequenceEqual(expectedRowVersion))
                return Fail(PrescriptionError.PrescriptionChanged);
            if (prescription.Status != PrescriptionStatus.Draft)
                return Fail(PrescriptionError.InvalidLifecycle);

            try
            {
                prescription.RemoveItem(itemId, actor, clock.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return Fail(PrescriptionError.InvalidInput);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return Fail(PrescriptionError.PrescriptionItemNotFound);
            }
            catch (InvalidOperationException)
            {
                return Fail(PrescriptionError.InvalidLifecycle);
            }

            store.ExpectVersion(prescription, expectedRowVersion);
            auditWriter.Append(new AuditAppendRequest(actor, "prescription.item.remove", "prescription",
                prescription.Id.ToString("N"), prescription.PatientId, AuditOutcome.Succeeded, null,
                [new KeyValuePair<string, string>("item.count", prescription.Items.Count.ToString())]));
            await store.SaveAsync(ct);
            return PrescriptionResult.Success(Map(prescription));
        }
        catch (PersistenceConcurrencyException)
        {
            return Fail(PrescriptionError.PrescriptionChanged);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public Task<PrescriptionResult> ReorderItemsAsync(
        Guid patientId,
        Guid prescriptionId,
        ReorderPrescriptionItemsRequest request,
        string actor,
        CancellationToken ct = default) =>
        Mutate(patientId, prescriptionId, request.ExpectedRowVersion, p => p.ReorderItems(request.OrderedItemIds, actor, clock.GetUtcNow()), ct,
            audit: p => new AuditAppendRequest(actor, "prescription.item.reorder", "prescription",
                p.Id.ToString("N"), p.PatientId, AuditOutcome.Succeeded, null, null));

    public async Task<PrescriptionResult> FinalizeAsync(
        Guid patientId,
        Guid prescriptionId,
        byte[] expectedRowVersion,
        string actor,
        CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (expectedRowVersion is not { Length: 8 })
            return Fail(PrescriptionError.InvalidRowVersion);

        try
        {
            var prescription = await store.GetAsync(patientId, prescriptionId, ct);
            if (prescription is null)
                return Fail(PrescriptionError.PrescriptionNotFound);
            if (!prescription.RowVersion.SequenceEqual(expectedRowVersion))
                return Fail(PrescriptionError.PrescriptionChanged);
            if (prescription.Status != PrescriptionStatus.Draft)
                return Fail(PrescriptionError.InvalidLifecycle);
            if (prescription.Items.Count == 0)
                return Fail(PrescriptionError.InvalidInput);
            if (prescription.Items.Any(x => !x.IsComplete()))
                return Fail(PrescriptionError.InvalidInput);

            var medIds = prescription.Items.Select(x => x.MedicationId).Distinct().ToList();

            // The eligibility check and the finalization save share one transaction, and the
            // catalog read takes update locks: a deactivation committed before this read is seen,
            // and one that has not committed yet waits until the finalization commits. A finalized
            // prescription therefore never references a medication deactivated before its commit.
            await using var scope = await store.BeginMedicationActivityScopeAsync(ct);
            var activeStatuses = await scope.ReadActiveStatusAsync(medIds, ct);

            foreach (var medId in medIds)
            {
                if (!activeStatuses.TryGetValue(medId, out var isActive) || !isActive)
                    return Fail(PrescriptionError.InactiveMedication);
            }

            try
            {
                prescription.FinalizePrescription(activeStatuses, actor, clock.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return Fail(PrescriptionError.InvalidInput);
            }
            catch (InvalidOperationException)
            {
                return Fail(PrescriptionError.InvalidLifecycle);
            }

            store.ExpectVersion(prescription, expectedRowVersion);
            auditWriter.Append(new AuditAppendRequest(actor, "prescription.finalize", "prescription",
                prescription.Id.ToString("N"), prescription.PatientId, AuditOutcome.Succeeded, null,
                [new KeyValuePair<string, string>("item.count", prescription.Items.Count.ToString())]));
            await store.SaveAsync(ct);
            await scope.CommitAsync(ct);
            return PrescriptionResult.Success(Map(prescription));
        }
        catch (PersistenceConcurrencyException)
        {
            return Fail(PrescriptionError.PrescriptionChanged);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public Task<PrescriptionResult> ReleaseAsync(
        Guid patientId,
        Guid prescriptionId,
        byte[] expectedRowVersion,
        string actor,
        CancellationToken ct = default) =>
        Mutate(patientId, prescriptionId, expectedRowVersion, p => p.ReleasePrescription(actor, clock.GetUtcNow()), ct,
            audit: p => new AuditAppendRequest(actor, "prescription.release", "prescription",
                p.Id.ToString("N"), p.PatientId, AuditOutcome.Succeeded, null, null));

    public Task<PrescriptionResult> CancelAsync(
        Guid patientId,
        Guid prescriptionId,
        CancelPrescriptionRequest request,
        string actor,
        CancellationToken ct = default) =>
        Mutate(patientId, prescriptionId, request.ExpectedRowVersion, p => p.CancelPrescription(request.Reason, actor, clock.GetUtcNow()), ct,
            // Cancellation free text is never audited.
            audit: p => new AuditAppendRequest(actor, "prescription.cancel", "prescription",
                p.Id.ToString("N"), p.PatientId, AuditOutcome.Succeeded, null, null));

    private async Task<PrescriptionResult> Mutate(
        Guid patientId,
        Guid prescriptionId,
        byte[] version,
        Action<Prescription> mutation,
        CancellationToken ct,
        Func<Prescription, AuditAppendRequest>? audit = null)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (version is not { Length: 8 })
            return Fail(PrescriptionError.InvalidRowVersion);

        try
        {
            var prescription = await store.GetAsync(patientId, prescriptionId, ct);
            if (prescription is null)
                return Fail(PrescriptionError.PrescriptionNotFound);
            if (!prescription.RowVersion.SequenceEqual(version))
                return Fail(PrescriptionError.PrescriptionChanged);

            try
            {
                mutation(prescription);
            }
            catch (ArgumentException)
            {
                return Fail(PrescriptionError.InvalidInput);
            }
            catch (InvalidOperationException)
            {
                return Fail(PrescriptionError.InvalidLifecycle);
            }

            store.ExpectVersion(prescription, version);
            if (audit is not null) auditWriter.Append(audit(prescription));
            await store.SaveAsync(ct);
            return PrescriptionResult.Success(Map(prescription));
        }
        catch (PersistenceConcurrencyException)
        {
            return Fail(PrescriptionError.PrescriptionChanged);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    private PrescriptionResult Fail(PrescriptionError error)
    {
        store.DiscardChanges();
        return PrescriptionResult.Failure(error);
    }

    private static PrescriptionDetails Map(Prescription p) => new(
        p.Id,
        p.VisitId,
        p.PatientId,
        p.DoctorId,
        p.Status,
        p.ReplacesPrescriptionId,
        p.ReplacedByPrescriptionId,
        p.Notes,
        p.FinalizedAtUtc,
        p.FinalizedByStaffId,
        p.ReleasedAtUtc,
        p.ReleasedByStaffId,
        p.CancellationReason,
        p.CancelledAtUtc,
        p.CancelledByStaffId,
        p.CreatedAtUtc,
        p.CreatedByStaffId,
        p.LastModifiedAtUtc,
        p.LastModifiedByStaffId,
        p.RowVersion.ToArray(),
        p.Items.OrderBy(x => x.DisplayOrder).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new PrescriptionItemDetails(
                x.Id,
                x.PrescriptionId,
                x.MedicationId,
                x.GenericNameEn,
                x.GenericNameAr,
                x.BrandNameEn,
                x.BrandNameAr,
                x.Strength,
                x.Unit,
                x.Form,
                x.Route,
                x.Dose,
                x.Frequency,
                x.Duration,
                x.Instructions,
                x.DisplayOrder,
                x.CreatedAtUtc,
                x.CreatedByStaffId)).ToArray());
}
