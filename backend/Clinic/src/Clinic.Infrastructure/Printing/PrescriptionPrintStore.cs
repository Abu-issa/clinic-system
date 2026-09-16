using Clinic.Application.Prescriptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Printing;

/// <summary>
/// Assembles the printable projection from current persisted records. Display identity
/// (patient name/MRN/DOB, doctor display name) is read from CURRENT Patient and Doctor rows,
/// not from a finalization-time snapshot: re-printing after an identity correction reflects the
/// corrected identity. This historical-reproduction limitation is documented in
/// docs/medications-prescriptions-phase-3.md. Medication content always comes from the
/// prescription item snapshots, never from the current catalog.
/// </summary>
public sealed class PrescriptionPrintStore(ClinicDbContext db) : IPrescriptionPrintStore
{
    public async Task<PrescriptionPrintProjection?> GetAsync(Guid patientId, Guid prescriptionId, CancellationToken ct)
    {
        var prescription = await db.Set<Prescription>().Include(x => x.Items).AsSingleQuery()
            .SingleOrDefaultAsync(x => x.Id == prescriptionId && x.PatientId == patientId, ct);
        if (prescription is null)
            return null;

        var patient = await db.Patients.AsNoTracking()
            .Where(x => x.Id == patientId)
            .Select(x => new { x.FullName, x.MedicalRecordNumber, x.DateOfBirth })
            .SingleOrDefaultAsync(ct);
        var doctor = await db.Doctors.AsNoTracking()
            .Where(x => x.Id == prescription.DoctorId)
            .Select(x => new { x.FullName })
            .SingleOrDefaultAsync(ct);
        if (patient is null || doctor is null)
            return null;

        return new PrescriptionPrintProjection(
            prescription.Id,
            prescription.Status,
            prescription.FinalizedAtUtc,
            patient.FullName,
            patient.MedicalRecordNumber,
            patient.DateOfBirth,
            doctor.FullName,
            prescription.Items
                .OrderBy(x => x.DisplayOrder).ThenBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                .Select(x => new PrescriptionPrintItemProjection(
                    x.DisplayOrder, x.GenericNameEn, x.GenericNameAr, x.BrandNameEn, x.BrandNameAr,
                    x.Strength, x.Unit, x.Form, x.Route, x.Dose, x.Frequency, x.Duration, x.Instructions))
                .ToArray());
    }
}
