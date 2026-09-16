using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Medications;

/// <summary>
/// Application service for Medication Catalog administration and search.
/// Optimistic concurrency via RowVersion. Changes are transactional and safely detached on error.
/// </summary>
public sealed class MedicationCatalogService(IMedicationCatalogStore store, TimeProvider clock, IAuditMutationWriter auditWriter)
{
    public async Task<MedicationCatalogResult> CreateAsync(CreateMedicationRequest request, string actor, CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (string.IsNullOrWhiteSpace(actor))
            return Fail(MedicationCatalogError.InvalidInput);

        try
        {
            var now = clock.GetUtcNow();
            var medication = new Medication(
                request.GenericNameEn,
                request.GenericNameAr,
                request.BrandNameEn,
                request.BrandNameAr,
                request.Strength,
                request.Unit,
                request.Form,
                request.Route,
                request.Category,
                actor,
                now);

            // A catalog identity is the generic name with strength, unit, form and route; brand
            // names and category do not distinguish products. Inactive entries still count, so a
            // withdrawn medication cannot be silently re-created as a fresh active duplicate.
            if (await store.ExistsDuplicateAsync(medication, ct))
                return Fail(MedicationCatalogError.DuplicateMedication);

            store.Add(medication);
            auditWriter.Append(new AuditAppendRequest(actor, "medication.create", "medication",
                medication.Id.ToString("N"), null, AuditOutcome.Succeeded, null, null));
            await store.SaveAsync(ct);
            return MedicationCatalogResult.Success(Map(medication));
        }
        catch (ArgumentException)
        {
            return Fail(MedicationCatalogError.InvalidInput);
        }
        catch (MedicationCatalogConflictException)
        {
            return Fail(MedicationCatalogError.DuplicateMedication);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public async Task<MedicationCatalogResult> GetAsync(Guid id, CancellationToken ct = default)
    {
        var medication = await store.GetByIdAsync(id, ct);
        return medication is null
            ? MedicationCatalogResult.Failure(MedicationCatalogError.MedicationNotFound)
            : MedicationCatalogResult.Success(Map(medication));
    }

    public async Task<MedicationSearchResult> SearchAsync(MedicationSearchQuery query, CancellationToken ct = default)
    {
        if (query.Skip < 0 || query.Take is < 1 or > 100)
            return new(false, MedicationCatalogError.InvalidInput, [], 0);

        try
        {
            var (items, total) = await store.SearchAsync(query, ct);
            return new(true, MedicationCatalogError.None, items, total);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    public Task<MedicationCatalogResult> UpdateAsync(Guid id, UpdateMedicationRequest request, string actor, CancellationToken ct = default) =>
        Mutate(id, request.ExpectedRowVersion, m => m.UpdateDetails(
            request.GenericNameEn,
            request.GenericNameAr,
            request.BrandNameEn,
            request.BrandNameAr,
            request.Strength,
            request.Unit,
            request.Form,
            request.Route,
            request.Category,
            actor,
            clock.GetUtcNow()), ct, actor, "medication.update", null);

    public Task<MedicationCatalogResult> DeactivateAsync(Guid id, byte[] expectedRowVersion, string actor, CancellationToken ct = default) =>
        Mutate(id, expectedRowVersion, m => m.Deactivate(actor, clock.GetUtcNow()), ct, actor, "medication.deactivate", null);

    public Task<MedicationCatalogResult> ActivateAsync(Guid id, byte[] expectedRowVersion, string actor, CancellationToken ct = default) =>
        Mutate(id, expectedRowVersion, m => m.Activate(actor, clock.GetUtcNow()), ct, actor, "medication.activate", null);

    private async Task<MedicationCatalogResult> Mutate(Guid id, byte[] version, Action<Medication> mutation, CancellationToken ct,
        string actor, string auditAction, IReadOnlyList<KeyValuePair<string, string>>? auditMetadata)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (version is not { Length: 8 })
            return Fail(MedicationCatalogError.InvalidRowVersion);

        try
        {
            var medication = await store.GetByIdAsync(id, ct);
            if (medication is null)
                return Fail(MedicationCatalogError.MedicationNotFound);
            if (!medication.RowVersion.SequenceEqual(version))
                return Fail(MedicationCatalogError.MedicationChanged);

            try
            {
                mutation(medication);
            }
            catch (ArgumentException)
            {
                return Fail(MedicationCatalogError.InvalidInput);
            }
            catch (InvalidOperationException)
            {
                return Fail(MedicationCatalogError.InvalidInput);
            }

            store.ExpectVersion(medication, version);
            auditWriter.Append(new AuditAppendRequest(actor, auditAction, "medication",
                medication.Id.ToString("N"), null, AuditOutcome.Succeeded, null, auditMetadata));
            await store.SaveAsync(ct);
            return MedicationCatalogResult.Success(Map(medication));
        }
        catch (PersistenceConcurrencyException)
        {
            return Fail(MedicationCatalogError.MedicationChanged);
        }
        catch
        {
            store.DiscardChanges();
            throw;
        }
    }

    private MedicationCatalogResult Fail(MedicationCatalogError error)
    {
        store.DiscardChanges();
        return MedicationCatalogResult.Failure(error);
    }

    private static MedicationDetails Map(Medication m) => new(
        m.Id,
        m.GenericNameEn,
        m.GenericNameAr,
        m.BrandNameEn,
        m.BrandNameAr,
        m.Strength,
        m.Unit,
        m.Form,
        m.Route,
        m.Category,
        m.IsActive,
        m.CreatedAtUtc,
        m.CreatedByStaffId,
        m.LastModifiedAtUtc,
        m.LastModifiedByStaffId,
        m.RowVersion.ToArray());
}
