using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class PrescriptionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid VisitId = Guid.NewGuid();
    private static readonly Guid PatientId = Guid.NewGuid();
    private static readonly Guid DoctorId = Guid.NewGuid();

    private static Medication Medication(string strength = "500", string unit = "mg") =>
        new("Ibuprofen", "إيبوبروفين", null, null, strength, unit, DosageForm.Tablet, MedicationRoute.Oral,
            "Analgesic", "admin", Now);

    private static Prescription Draft(string? notes = null) =>
        new(VisitId, PatientId, DoctorId, notes, null, "doctor", Now);

    [Fact]
    public void DraftItemMayBeIncompleteButStaysBounded()
    {
        var p = Draft();
        var item = p.AddItem(Medication(), null, null, null, null, null, "doctor", Now);
        Assert.False(item.IsComplete());
        Assert.Null(item.Dose);
        Assert.Null(item.Frequency);
        Assert.Null(item.Duration);

        // Whitespace-only input is normalized to null, not kept as a fake value.
        var item2 = p.AddItem(Medication(), "  ", "\t", " \n", "", 1, "doctor", Now);
        Assert.Null(item2.Dose);
        Assert.Null(item2.Frequency);

        // Incomplete is allowed; oversized is not, in every field, at any time.
        Assert.Throws<ArgumentException>(() => p.AddItem(Medication(), new string('x', 201), null, null, null, null, "doctor", Now));
        Assert.Throws<ArgumentException>(() => p.AddItem(Medication(), null, new string('x', 201), null, null, null, "doctor", Now));
        Assert.Throws<ArgumentException>(() => p.AddItem(Medication(), null, null, new string('x', 201), null, null, "doctor", Now));
        Assert.Throws<ArgumentException>(() => p.AddItem(Medication(), null, null, null, new string('x', 1001), null, "doctor", Now));
        // PrescriptionItem.Update is internal: oversized values can only be attempted through the aggregate.
        Assert.Throws<ArgumentException>(() => p.UpdateItem(item.Id, null, new string('x', 201), null, null, null, null, "doctor", Now));
    }

    [Fact]
    public void FinalizeRejectsIncompleteItemsAndRequiresEveryMedicationActive()
    {
        var p = Draft();
        var med = Medication();
        p.AddItem(med, "1 tablet", null, null, null, null, "doctor", Now);
        var active = new Dictionary<Guid, bool> { [med.Id] = true };
        Assert.Throws<InvalidOperationException>(() => p.FinalizePrescription(active, "doctor", Now));

        p.UpdateItem(p.Items.Single().Id, null, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        p.FinalizePrescription(active, "doctor", Now);
        Assert.Equal(PrescriptionStatus.Finalized, p.Status);
        Assert.Equal("doctor", p.FinalizedByStaffId);
        Assert.Equal(Now, p.FinalizedAtUtc);
    }

    [Fact]
    public void FinalizeRejectsInactiveOrUnknownMedication()
    {
        var p = Draft();
        var med = Medication();
        p.AddItem(med, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        var inactive = new Dictionary<Guid, bool> { [med.Id] = false };
        Assert.Throws<InvalidOperationException>(() => p.FinalizePrescription(inactive, "doctor", Now));
        Assert.Throws<InvalidOperationException>(() => p.FinalizePrescription(new Dictionary<Guid, bool>(), "doctor", Now));
        Assert.Equal(PrescriptionStatus.Draft, p.Status);
    }

    [Fact]
    public void InactiveMedicationCannotBeAddedOrReselected()
    {
        var p = Draft();
        var med = Medication();
        med.Deactivate("admin", Now);
        Assert.Throws<InvalidOperationException>(() => p.AddItem(med, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now));

        var active = Medication();
        var item = p.AddItem(active, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        active.Deactivate("admin", Now);
        Assert.Throws<InvalidOperationException>(() => p.UpdateItem(item.Id, active, "2 tablets", null, null, null, null, "doctor", Now));
    }

    [Fact]
    public void ItemMutationsOnlyHappenThroughTheAggregateAndAreBlockedAfterFinalization()
    {
        var p = Draft();
        var item = p.AddItem(Medication(), "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);

        // Items expose no public mutators; the collection is read-only.
        Assert.Throws<NotSupportedException>(() => ((ICollection<PrescriptionItem>)p.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PrescriptionItem>)p.Items).Remove(item));

        p.FinalizePrescription(new Dictionary<Guid, bool> { [item.MedicationId] = true }, "doctor", Now);
        Assert.Throws<InvalidOperationException>(() => p.UpdateItem(item.Id, null, "2", null, null, null, null, "doctor", Now));
        Assert.Throws<InvalidOperationException>(() => p.RemoveItem(item.Id, "doctor", Now));
        Assert.Throws<InvalidOperationException>(() => p.AddItem(Medication(), null, null, null, null, null, "doctor", Now));
        Assert.Throws<InvalidOperationException>(() => p.UpdateDraftNotes("x", "doctor", Now));
        Assert.Equal("1 tablet", p.Items.Single().Dose);
    }

    [Fact]
    public void ItemMutationAdvancesAggregateMetadata()
    {
        var p = Draft();
        Assert.Equal(Now, p.LastModifiedAtUtc);
        p.AddItem(Medication(), null, null, null, null, null, "doctor", Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(5), p.LastModifiedAtUtc);
        Assert.Equal("doctor", p.LastModifiedByStaffId);
    }

    [Fact]
    public void DraftItemRemovalAndReorderWork()
    {
        var p = Draft();
        var a = p.AddItem(Medication(), "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        var b = p.AddItem(Medication("10", "mg"), "5 ml", "every 6 hours", "2 weeks", null, null, "doctor", Now);
        p.RemoveItem(a.Id, "doctor", Now.AddMinutes(1));
        Assert.Single(p.Items, b);
        p.ReorderItems([b.Id], "doctor", Now.AddMinutes(2));
        Assert.Equal(0, b.DisplayOrder);
        Assert.Throws<ArgumentException>(() => p.ReorderItems([], "doctor", Now));
        Assert.Throws<ArgumentException>(() => p.ReorderItems([b.Id, b.Id], "doctor", Now));
        Assert.Throws<ArgumentException>(() => p.ReorderItems([Guid.NewGuid()], "doctor", Now));
    }

    [Fact]
    public void CatalogSnapshotIsCapturedAtInsertionAndReselectionRefreshesIt()
    {
        var p = Draft();
        var med = Medication();
        var item = p.AddItem(med, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        Assert.Equal("Ibuprofen", item.GenericNameEn);
        Assert.Equal("إيبوبروفين", item.GenericNameAr);
        Assert.Equal("500", item.Strength);
        Assert.Equal("mg", item.Unit);
        Assert.Equal(DosageForm.Tablet, item.Form);
        Assert.Equal(MedicationRoute.Oral, item.Route);

        med.UpdateDetails("Ibuprofen", null, "Advil", null, "400", "mg", DosageForm.Capsule, MedicationRoute.Oral,
            null, "admin", Now.AddMinutes(1));
        // Catalog edits never leak into captured snapshots.
        Assert.Equal("500", item.Strength);
        p.UpdateItem(item.Id, med, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        Assert.Equal("400", item.Strength);
        Assert.Equal("Advil", item.BrandNameEn);
        Assert.Equal(DosageForm.Capsule, item.Form);
    }

    [Fact]
    public void LifecycleGuardsMatchTheDocumentedContract()
    {
        var p = Draft();
        Assert.Throws<InvalidOperationException>(() => p.ReleasePrescription("doctor", Now));
        Assert.Throws<InvalidOperationException>(() => p.CancelPrescription("mistake", "doctor", Now));

        var item = p.AddItem(Medication(), "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        p.FinalizePrescription(new Dictionary<Guid, bool> { [item.MedicationId] = true }, "doctor", Now);

        p.ReleasePrescription("pharmacy", Now.AddMinutes(1));
        p.ReleasePrescription("pharmacy", Now.AddMinutes(2)); // idempotent
        Assert.Equal(Now.AddMinutes(1), p.ReleasedAtUtc);
        Assert.Equal(PrescriptionStatus.Released, p.Status);

        p.CancelPrescription("dosage error", "doctor", Now.AddMinutes(3));
        p.CancelPrescription("again", "doctor", Now.AddMinutes(4)); // idempotent
        Assert.Equal("dosage error", p.CancellationReason);
        Assert.Equal(Now.AddMinutes(3), p.CancelledAtUtc);
        Assert.Throws<InvalidOperationException>(() => p.ReleasePrescription("pharmacy", Now));
    }

    [Fact]
    public void CancellationRequiresBoundedReason()
    {
        var p = Draft();
        var item = p.AddItem(Medication(), "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        p.FinalizePrescription(new Dictionary<Guid, bool> { [item.MedicationId] = true }, "doctor", Now);
        Assert.Throws<ArgumentException>(() => p.CancelPrescription(" ", "doctor", Now));
        Assert.Throws<ArgumentException>(() => p.CancelPrescription(new string('x', 1001), "doctor", Now));
    }

    [Fact]
    public void ReplacedByLinkIsConsistentAndImmutable()
    {
        var p = Draft();
        var item = p.AddItem(Medication(), "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        p.FinalizePrescription(new Dictionary<Guid, bool> { [item.MedicationId] = true }, "doctor", Now);
        p.CancelPrescription("wrong drug", "doctor", Now);

        var replacementId = Guid.NewGuid();
        p.SetReplacedBy(replacementId, "doctor", Now);
        p.SetReplacedBy(replacementId, "doctor", Now); // idempotent relink of same replacement
        var other = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => p.SetReplacedBy(other, "doctor", Now));
        Assert.Equal(replacementId, p.ReplacedByPrescriptionId);
    }

    [Fact]
    public void SetReplacedByRequiresCancelledStatus()
    {
        var p = Draft();
        Assert.Throws<InvalidOperationException>(() => p.SetReplacedBy(Guid.NewGuid(), "doctor", Now));
        var item = p.AddItem(Medication(), "1 tablet", "twice daily", "7 days", null, null, "doctor", Now);
        p.FinalizePrescription(new Dictionary<Guid, bool> { [item.MedicationId] = true }, "doctor", Now);
        Assert.Throws<InvalidOperationException>(() => p.SetReplacedBy(Guid.NewGuid(), "doctor", Now));
    }

    [Fact]
    public void StrengthAndUnitAreSeparateTokens()
    {
        // The unit is never embedded in the strength: "500 mg" plus Unit="mg" would be ambiguous.
        Assert.Throws<ArgumentException>(() => Medication("500 mg", "mg"));
        Assert.Throws<ArgumentException>(() => Medication("500", "mg ml"));
        // Compound strengths are "/"-separated values with a shared unit token.
        var compound = Medication("500/125", "mg");
        Assert.Equal("500/125", compound.Strength);
        // Ratio units keep the value/unit pair unambiguous.
        var ratio = Medication("5", "mg/ml");
        Assert.Equal("mg/ml", ratio.Unit);
        Assert.Throws<ArgumentException>(() => Medication("", "mg"));
        Assert.Throws<ArgumentException>(() => Medication("500", ""));
        Assert.Throws<ArgumentException>(() => Medication(new string('x', 101), "mg"));
        Assert.Throws<ArgumentException>(() => Medication("500", new string('x', 51)));
    }

    [Fact]
    public void MedicationDeactivationAndActivationAreIdempotent()
    {
        var m = Medication();
        Assert.True(m.IsActive);
        m.Deactivate("admin", Now);
        m.Deactivate("admin", Now);
        Assert.False(m.IsActive);
        Assert.Equal(Now, m.LastModifiedAtUtc);
        m.Activate("admin", Now.AddMinutes(1));
        m.Activate("admin", Now.AddMinutes(2));
        Assert.True(m.IsActive);
        Assert.Equal(Now.AddMinutes(1), m.LastModifiedAtUtc);
    }

    [Fact]
    public void ItemCountLimitIsEnforcedAtTheDomainBoundaryWithoutPartialMutation()
    {
        var p = Draft();
        var med = Medication();
        for (var i = 0; i < Prescription.MaxItemCount; i++)
            p.AddItem(med, "1 tablet", "twice daily", "7 days", null, i, "doctor", Now.AddMinutes(i));

        Assert.Equal(Prescription.MaxItemCount, p.Items.Count);

        // The rejected 201st add leaves no partial state behind.
        var lastModified = p.LastModifiedAtUtc;
        Assert.Throws<ArgumentException>(() =>
            p.AddItem(med, "1 tablet", "twice daily", "7 days", null, null, "doctor", Now.AddHours(1)));
        Assert.Equal(Prescription.MaxItemCount, p.Items.Count);
        Assert.Equal(lastModified, p.LastModifiedAtUtc);

        // Exactly the maximum is still complete-able, finalizable, and consistent.
        p.FinalizePrescription(new Dictionary<Guid, bool> { [med.Id] = true }, "doctor", Now.AddHours(2));
        Assert.Equal(PrescriptionStatus.Finalized, p.Status);
        Assert.Equal(Prescription.MaxItemCount, p.Items.Count);
    }

    [Fact]
    public void PrescriptionRequiresValidReferences()
    {
        Assert.Throws<ArgumentException>(() => new Prescription(Guid.Empty, PatientId, DoctorId, null, null, "doctor", Now));
        Assert.Throws<ArgumentException>(() => new Prescription(VisitId, Guid.Empty, DoctorId, null, null, "doctor", Now));
        Assert.Throws<ArgumentException>(() => new Prescription(VisitId, PatientId, Guid.Empty, null, null, "doctor", Now));
        Assert.Throws<ArgumentException>(() => new Prescription(VisitId, PatientId, DoctorId, null, Guid.Empty, "doctor", Now));
        Assert.Throws<ArgumentException>(() => new Prescription(VisitId, PatientId, DoctorId, new string('x', 2001), null, "doctor", Now));
    }
}
