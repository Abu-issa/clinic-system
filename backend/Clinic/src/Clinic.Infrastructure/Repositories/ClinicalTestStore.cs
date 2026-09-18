using Clinic.Application.ClinicalTests;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class ClinicalTestStore(ClinicDbContext db) : IClinicalTestStore
{
    public Task<bool> PatientExistsAsync(Guid patientId, CancellationToken ct) => db.Patients.AnyAsync(x => x.Id == patientId, ct);
    public Task<ClinicalTestPatientContext?> PatientContextAsync(Guid patientId, CancellationToken ct) => db.Patients.AsNoTracking()
        .Where(x => x.Id == patientId).Select(x => new ClinicalTestPatientContext(x.Id, x.FullName, x.MedicalRecordNumber)).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<ClinicalTestVisitChoice>> VisitChoicesAsync(Guid patientId, Guid doctorId, CancellationToken ct) =>
        await db.Set<Visit>().AsNoTracking().Where(x => x.PatientId == patientId && x.DoctorId == doctorId)
            .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Take(100)
            .Select(x => new ClinicalTestVisitChoice(x.Id, x.OccurredAtUtc)).ToArrayAsync(ct);
    public Task<Guid?> AssociatedDoctorAsync(string actorStaffId, CancellationToken ct) => db.Users.AsNoTracking()
        .Where(x => x.Id == actorStaffId).Select(x => x.AssociatedDoctorId).SingleOrDefaultAsync(ct);
    public Task<Guid?> VisitDoctorAsync(Guid patientId, Guid visitId, CancellationToken ct) => db.Set<Visit>().AsNoTracking()
        .Where(x => x.Id == visitId && x.PatientId == patientId).Select(x => (Guid?)x.DoctorId).SingleOrDefaultAsync(ct);
    public Task<ClinicalTestDetails?> GetAsync(Guid patientId, Guid requestId, CancellationToken ct) =>
        db.Set<ClinicalTestRequest>().AsNoTracking().Where(x => x.PatientId == patientId && x.Id == requestId)
            .Select(x => new ClinicalTestDetails(x.Id, x.PatientId, x.VisitId, x.Category, x.TestName,
                x.ClinicalInstructions, x.Status, x.RequestedAtUtc, x.UploadedAtUtc, x.ReviewedAtUtc,
                x.RowVersion, db.Set<ClinicalTestResultAttachment>().Count(r => r.ClinicalTestRequestId == x.Id), null)).SingleOrDefaultAsync(ct);
    public Task<ClinicalTestRequest?> LoadAsync(Guid patientId, Guid requestId, CancellationToken ct) =>
        db.Set<ClinicalTestRequest>().SingleOrDefaultAsync(x => x.PatientId == patientId && x.Id == requestId, ct);
    public async Task<IReadOnlyList<ClinicalTestAttachmentDetails>> ResultsAsync(Guid patientId, Guid requestId, CancellationToken ct) =>
        await (from link in db.Set<ClinicalTestResultAttachment>().AsNoTracking()
               join request in db.Set<ClinicalTestRequest>() on link.ClinicalTestRequestId equals request.Id
               join attachment in db.Set<PatientAttachment>() on link.PatientAttachmentId equals attachment.Id
               where request.Id == requestId && request.PatientId == patientId && attachment.PatientId == patientId
               orderby link.LinkedAtUtc, link.Id
               select new ClinicalTestAttachmentDetails(attachment.Id, attachment.File.OriginalFileName,
                   attachment.File.ContentType, attachment.File.SizeBytes, attachment.File.CreatedAtUtc)).ToArrayAsync(ct);
    public void ExpectVersion(ClinicalTestRequest request, byte[] expected)
    {
        db.Entry(request).Property(x => x.RowVersion).OriginalValue = expected;
        // Additional uploads must issue an UPDATE even when status/time stay unchanged.
        db.Entry(request).Property(x => x.Status).IsModified = true;
    }
    public void AddResult(ClinicalTestResultAttachment result) => db.Add(result);
    public async Task<IReadOnlyList<ClinicalTestSummary>> ListAsync(Guid patientId, ClinicalTestCategory? category,
        ClinicalTestStatus? status, int page, int pageSize, CancellationToken ct) =>
        await db.Set<ClinicalTestRequest>().AsNoTracking()
            .Where(x => x.PatientId == patientId && (category == null || x.Category == category) && (status == null || x.Status == status))
            .OrderByDescending(x => x.RequestedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ClinicalTestSummary(x.Id, x.PatientId, x.VisitId, x.Category, x.TestName, x.Status, x.RequestedAtUtc,
                x.UploadedAtUtc, x.ReviewedAtUtc, db.Set<ClinicalTestResultAttachment>().Count(r => r.ClinicalTestRequestId == x.Id)))
            .ToArrayAsync(ct);
    public void Add(ClinicalTestRequest request) => db.Add(request);
    public async Task SaveAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    public void DiscardChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is ClinicalTestRequest or ClinicalTestResultAttachment)
                     .OrderBy(x => x.Entity is ClinicalTestResultAttachment ? 0 : 1).ToArray())
            entry.State = EntityState.Detached;
    }
}
