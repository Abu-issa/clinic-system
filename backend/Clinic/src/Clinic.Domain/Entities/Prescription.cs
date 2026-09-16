using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

/// <summary>
/// Prescription aggregate root. Belongs to a Visit; derives PatientId and DoctorId from Visit.
/// Has an independent lifecycle: Draft -> Finalized -> Released -> Cancelled.
/// </summary>
public sealed class Prescription
{
    private readonly List<PrescriptionItem> _items = [];

    /// <summary>
    /// Authoritative maximum number of items a prescription may carry. Enforced at item addition
    /// so no application-supported path can produce a Finalized/Released prescription that the
    /// print pipeline (which reuses this same limit defensively) cannot represent. Direct
    /// privileged SQL writes remain outside the application guarantee.
    /// </summary>
    public const int MaxItemCount = 200;

    public Guid Id { get; private set; }

    /// <summary>The Visit encounter this prescription belongs to.</summary>
    public Guid VisitId { get; private set; }

    /// <summary>Derived strictly from Visit.PatientId.</summary>
    public Guid PatientId { get; private set; }

    /// <summary>Derived strictly from Visit.DoctorId (prescribing doctor).</summary>
    public Guid DoctorId { get; private set; }

    /// <summary>Current lifecycle status.</summary>
    public PrescriptionStatus Status { get; private set; }

    /// <summary>If this prescription replaces a cancelled prescription (for corrections).</summary>
    public Guid? ReplacesPrescriptionId { get; private set; }

    /// <summary>If this prescription was cancelled and replaced by a newer prescription.</summary>
    public Guid? ReplacedByPrescriptionId { get; private set; }

    /// <summary>Optional clinician notes or instructions for the overall prescription.</summary>
    public string? Notes { get; private set; }

    // Finalization audit
    public DateTimeOffset? FinalizedAtUtc { get; private set; }
    public string? FinalizedByStaffId { get; private set; }

    // Release audit
    public DateTimeOffset? ReleasedAtUtc { get; private set; }
    public string? ReleasedByStaffId { get; private set; }

    // Cancellation audit
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }
    public string? CancelledByStaffId { get; private set; }

    // Aggregate metadata
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByStaffId { get; private set; } = string.Empty;
    public DateTimeOffset LastModifiedAtUtc { get; private set; }
    public string LastModifiedByStaffId { get; private set; } = string.Empty;

    /// <summary>SQL Server RowVersion for aggregate-level optimistic concurrency.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<PrescriptionItem> Items => _items.AsReadOnly();

    private Prescription() { } // EF Core

    /// <summary>
    /// Creates a new prescription draft associated with a Visit.
    /// PatientId and DoctorId are derived directly from the Visit.
    /// </summary>
    public Prescription(
        Guid visitId,
        Guid patientId,
        Guid doctorId,
        string? notes,
        Guid? replacesPrescriptionId,
        string createdByStaffId,
        DateTimeOffset createdAtUtc)
    {
        if (visitId == Guid.Empty)
            throw new ArgumentException("Visit ID is required.", nameof(visitId));
        if (patientId == Guid.Empty)
            throw new ArgumentException("Patient ID is required.", nameof(patientId));
        if (doctorId == Guid.Empty)
            throw new ArgumentException("Doctor ID is required.", nameof(doctorId));
        if (replacesPrescriptionId == Guid.Empty)
            throw new ArgumentException("Replacement must reference an existing prescription.", nameof(replacesPrescriptionId));
        if (string.IsNullOrWhiteSpace(createdByStaffId))
            throw new ArgumentException("Creator staff ID is required.", nameof(createdByStaffId));
        if (!string.IsNullOrEmpty(notes) && notes.Length > 2000)
            throw new ArgumentException("Notes must not exceed 2000 characters.", nameof(notes));

        Id = Guid.NewGuid();
        VisitId = visitId;
        PatientId = patientId;
        DoctorId = doctorId;
        Status = PrescriptionStatus.Draft;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ReplacesPrescriptionId = replacesPrescriptionId;
        CreatedByStaffId = createdByStaffId;
        CreatedAtUtc = createdAtUtc;
        LastModifiedByStaffId = createdByStaffId;
        LastModifiedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Update draft notes. Only allowed in Draft status.
    /// </summary>
    public void UpdateDraftNotes(string? notes, string modifiedByStaffId, DateTimeOffset modifiedAtUtc)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));
        if (!string.IsNullOrEmpty(notes) && notes.Length > 2000)
            throw new ArgumentException("Notes must not exceed 2000 characters.", nameof(notes));

        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    /// <summary>
    /// Add an item to the draft prescription, capturing a catalog snapshot.
    /// Dose, frequency and duration may be incomplete (null) while the prescription is a Draft;
    /// structural completeness is enforced by <see cref="FinalizePrescription"/>.
    /// </summary>
    public PrescriptionItem AddItem(
        Medication medication,
        string? dose,
        string? frequency,
        string? duration,
        string? instructions,
        int? displayOrder,
        string addedByStaffId,
        DateTimeOffset addedAtUtc)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(medication);

        if (_items.Count >= MaxItemCount)
            throw new ArgumentException(
                $"A prescription cannot contain more than {MaxItemCount} items.", nameof(displayOrder));
        if (!medication.IsActive)
            throw new InvalidOperationException("Cannot add inactive medication to prescription.");

        int order = displayOrder ?? _items.Count;
        var item = new PrescriptionItem(
            Id,
            medication,
            dose,
            frequency,
            duration,
            instructions,
            order,
            addedByStaffId,
            addedAtUtc);

        _items.Add(item);
        LastModifiedByStaffId = addedByStaffId;
        LastModifiedAtUtc = addedAtUtc;
        return item;
    }

    /// <summary>
    /// Update an existing item in the draft prescription. Dose, frequency and duration may remain
    /// incomplete while the prescription is a Draft.
    /// </summary>
    public void UpdateItem(
        Guid itemId,
        Medication? reselectedMedication,
        string? dose,
        string? frequency,
        string? duration,
        string? instructions,
        int? displayOrder,
        string modifiedByStaffId,
        DateTimeOffset modifiedAtUtc)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));

        var item = _items.FirstOrDefault(x => x.Id == itemId)
            ?? throw new InvalidOperationException($"Prescription item '{itemId}' was not found in this prescription.");

        if (reselectedMedication != null && !reselectedMedication.IsActive)
            throw new InvalidOperationException("Cannot reselect an inactive medication.");

        int order = displayOrder ?? item.DisplayOrder;
        item.Update(reselectedMedication, dose, frequency, duration, instructions, order);

        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    /// <summary>
    /// Remove an item from the draft prescription.
    /// </summary>
    public void RemoveItem(Guid itemId, string modifiedByStaffId, DateTimeOffset modifiedAtUtc)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));

        var item = _items.FirstOrDefault(x => x.Id == itemId)
            ?? throw new InvalidOperationException($"Prescription item '{itemId}' was not found in this prescription.");

        _items.Remove(item);
        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    /// <summary>
    /// Reorder items within the draft prescription.
    /// </summary>
    public void ReorderItems(IReadOnlyList<Guid> orderedItemIds, string modifiedByStaffId, DateTimeOffset modifiedAtUtc)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));
        ArgumentNullException.ThrowIfNull(orderedItemIds);

        if (orderedItemIds.Count != _items.Count || orderedItemIds.Distinct().Count() != _items.Count)
            throw new ArgumentException("Ordered item list must match the existing items exactly.", nameof(orderedItemIds));

        for (int i = 0; i < orderedItemIds.Count; i++)
        {
            var item = _items.FirstOrDefault(x => x.Id == orderedItemIds[i])
                ?? throw new ArgumentException($"Item '{orderedItemIds[i]}' not found in prescription.", nameof(orderedItemIds));
            item.Update(null, item.Dose, item.Frequency, item.Duration, item.Instructions, i);
        }

        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    /// <summary>
    /// Finalize the prescription. Requires at least 1 complete item and all referenced medications to be active.
    /// Finalized items become immutable.
    /// </summary>
    public void FinalizePrescription(
        IReadOnlyDictionary<Guid, bool> medicationActiveStatus,
        string finalizedByStaffId,
        DateTimeOffset finalizedAtUtc)
    {
        EnsureDraft();
        if (string.IsNullOrWhiteSpace(finalizedByStaffId))
            throw new ArgumentException("Finalizer staff ID is required.", nameof(finalizedByStaffId));
        if (_items.Count == 0)
            throw new InvalidOperationException("Cannot finalize a prescription with no items.");

        foreach (var item in _items)
        {
            if (!item.IsComplete())
                throw new InvalidOperationException($"Prescription item '{item.Id}' is incomplete (dose, frequency, and duration are required).");

            if (!medicationActiveStatus.TryGetValue(item.MedicationId, out var isActive) || !isActive)
                throw new InvalidOperationException($"Referenced medication '{item.MedicationId}' is inactive or not found in the catalog.");
        }

        Status = PrescriptionStatus.Finalized;
        FinalizedByStaffId = finalizedByStaffId;
        FinalizedAtUtc = finalizedAtUtc;
        LastModifiedByStaffId = finalizedByStaffId;
        LastModifiedAtUtc = finalizedAtUtc;
    }

    /// <summary>
    /// Release the prescription to the patient. Requires Finalized status.
    /// </summary>
    public void ReleasePrescription(string releasedByStaffId, DateTimeOffset releasedAtUtc)
    {
        if (Status == PrescriptionStatus.Cancelled)
            throw new InvalidOperationException("Cannot release a cancelled prescription.");
        if (Status == PrescriptionStatus.Draft)
            throw new InvalidOperationException("Cannot release a draft prescription. It must be finalized first.");
        if (Status == PrescriptionStatus.Released)
            return; // Idempotent

        if (string.IsNullOrWhiteSpace(releasedByStaffId))
            throw new ArgumentException("Releaser staff ID is required.", nameof(releasedByStaffId));

        Status = PrescriptionStatus.Released;
        ReleasedByStaffId = releasedByStaffId;
        ReleasedAtUtc = releasedAtUtc;
        LastModifiedByStaffId = releasedByStaffId;
        LastModifiedAtUtc = releasedAtUtc;
    }

    /// <summary>
    /// Cancel a finalized or released prescription. Requires cancellation reason.
    /// Preserves original content and release history.
    /// </summary>
    public void CancelPrescription(string reason, string cancelledByStaffId, DateTimeOffset cancelledAtUtc)
    {
        if (Status == PrescriptionStatus.Draft)
            throw new InvalidOperationException("Cannot cancel a draft prescription. Cancel applies to finalized/released clinical records.");
        if (Status == PrescriptionStatus.Cancelled)
            return; // Idempotent

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Cancellation reason is required.", nameof(reason));
        if (reason.Length > 1000)
            throw new ArgumentException("Cancellation reason must not exceed 1000 characters.", nameof(reason));
        if (string.IsNullOrWhiteSpace(cancelledByStaffId))
            throw new ArgumentException("Canceller staff ID is required.", nameof(cancelledByStaffId));

        Status = PrescriptionStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancelledByStaffId = cancelledByStaffId;
        CancelledAtUtc = cancelledAtUtc;
        LastModifiedByStaffId = cancelledByStaffId;
        LastModifiedAtUtc = cancelledAtUtc;
    }

    /// <summary>
    /// Sets the ReplacedByPrescriptionId when a replacement prescription is created for this
    /// cancelled prescription. The replacement's own ReplacesPrescriptionId and this value are
    /// written together in one transaction; setting a different replacement after one is already
    /// recorded is rejected so the two stored directions cannot disagree.
    /// </summary>
    public void SetReplacedBy(Guid replacementPrescriptionId, string modifiedByStaffId, DateTimeOffset modifiedAtUtc)
    {
        if (Status != PrescriptionStatus.Cancelled)
            throw new InvalidOperationException("Only a cancelled prescription can be marked as replaced.");
        if (replacementPrescriptionId == Guid.Empty)
            throw new ArgumentException("Replacement prescription ID is required.", nameof(replacementPrescriptionId));
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));
        if (ReplacedByPrescriptionId == replacementPrescriptionId)
            return; // Idempotent re-link of the same replacement
        if (ReplacedByPrescriptionId != null)
            throw new InvalidOperationException("This prescription has already been replaced by a different prescription.");

        ReplacedByPrescriptionId = replacementPrescriptionId;
        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    private void EnsureDraft()
    {
        if (Status != PrescriptionStatus.Draft)
            throw new InvalidOperationException($"Operation only allowed on draft prescriptions. Current status is {Status}.");
    }
}
