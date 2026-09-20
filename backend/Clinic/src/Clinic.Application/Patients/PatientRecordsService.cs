using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Patients;

public sealed class PatientRecordsService(IPatientRecordsStore store, TimeProvider clock, IAuditMutationWriter auditWriter)
{
    // Scope is supplied only by server authorization, never by the HTTP body.
    public async Task<PatientContextPage?> SearchContextAsync(PatientContextSearchRequest input,
        IReadOnlyCollection<Guid> allowedPatients, CancellationToken ct = default)
    {
        var term = input.SearchTerm?.Trim();
        if (term is null || term.Length is < 2 or > 100 || term.Any(char.IsControl) ||
            input.Page is < 1 or > 100 || input.PageSize is < 1 or > 20) return null;
        if (allowedPatients.Count == 0) return new([], input.Page, input.PageSize, false);
        var items = await store.SearchContextAsync(term, allowedPatients,
            (input.Page - 1) * input.PageSize, input.PageSize + 1, ct);
        return new(items.Take(input.PageSize).ToArray(), input.Page, input.PageSize, items.Count > input.PageSize);
    }

    public async Task<PatientAdminResult> GetAsync(Guid id, CancellationToken ct = default)
    {
        var patient = await store.PatientAsync(id, ct);
        return patient is null ? AdminFailure(PatientAdminError.PatientNotFound) : PatientAdminResult.Success(Admin(patient));
    }

    public async Task<PatientAdminResult> CreateAsync(CreatePatientRequest input, string? actor = null, CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (string.IsNullOrWhiteSpace(input.MedicalRecordNumber) || input.MedicalRecordNumber.Trim().Length > 32)
            return AdminFailure(PatientAdminError.InvalidMedicalRecordNumber);
        var validation = ValidateContact(input.FullName, input.PhoneNumber, input.DateOfBirth,
            input.EmergencyContactName, input.EmergencyContactPhone, input.EmergencyContactRelation);
        if (validation != PatientAdminError.None) return AdminFailure(validation);
        Patient patient;
        try
        {
            patient = new Patient(input.MedicalRecordNumber, input.FullName, input.PhoneNumber, input.DateOfBirth,
                input.LegacyPaperFileNumber, input.LegacyCoverImageReference);
            patient.UpdateAdministrativeDetails(input.FullName, input.PhoneNumber, input.DateOfBirth,
                input.EmergencyContactName, input.EmergencyContactPhone, input.EmergencyContactRelation);
        }
        catch (ArgumentException) { return AdminFailure(PatientAdminError.InvalidLegacyReferences); }
        store.Add(patient);
        auditWriter.Append(new AuditAppendRequest(actor, "patient.create", "patient",
            patient.Id.ToString("N"), patient.Id, AuditOutcome.Succeeded, null, null));
        try { await store.SaveAsync(ct); }
        catch (PatientRecordConflictException) { return AdminFailure(PatientAdminError.MedicalRecordNumberAlreadyExists); }
        return PatientAdminResult.Success(Admin(patient));
    }

    public async Task<PatientAdminResult> UpdateAsync(Guid id, UpdatePatientRequest input, string? actor = null, CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (input.ExpectedRowVersion is not { Length: 8 }) return AdminFailure(PatientAdminError.InvalidRowVersion);
        var patient = await store.PatientAsync(id, ct);
        if (patient is null) return AdminFailure(PatientAdminError.PatientNotFound);
        if (!patient.RowVersion.SequenceEqual(input.ExpectedRowVersion)) return AdminFailure(PatientAdminError.PatientChanged);
        var validation = ValidateContact(input.FullName, input.PhoneNumber, input.DateOfBirth,
            input.EmergencyContactName, input.EmergencyContactPhone, input.EmergencyContactRelation);
        if (validation != PatientAdminError.None) return AdminFailure(validation);
        try { patient.UpdateAdministrativeDetails(input.FullName, input.PhoneNumber, input.DateOfBirth,
            input.EmergencyContactName, input.EmergencyContactPhone, input.EmergencyContactRelation); }
        catch (ArgumentException) { return AdminFailure(PatientAdminError.InvalidDateOfBirth); }
        store.ExpectVersion(patient, input.ExpectedRowVersion);
        auditWriter.Append(new AuditAppendRequest(actor, "patient.admin.update", "patient",
            patient.Id.ToString("N"), patient.Id, AuditOutcome.Succeeded, null, null));
        try { await store.SaveAsync(ct); }
        catch (PersistenceConcurrencyException) { return AdminFailure(PatientAdminError.PatientChanged); }
        return PatientAdminResult.Success(Admin(patient));
    }

    public async Task<MedicalProfileResult> GetProfileAsync(Guid id, CancellationToken ct = default)
    {
        if (await store.PatientAsync(id, ct) is null) return ProfileFailure(MedicalProfileError.PatientNotFound);
        var profile = await store.ProfileAsync(id, ct);
        return MedicalProfileResult.Success(profile is null ? MedicalProfileDetails.Empty(id) : Profile(profile));
    }

    // This is a doctor-only complete snapshot command. Removals require explicit entry IDs.
    // Superseded rows are never deleted. Clients must send every retained entry and the last aggregate RowVersion.
    public async Task<MedicalProfileResult> SaveProfileAsync(Guid id, SaveMedicalProfileRequest input, string staffId, CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        if (string.IsNullOrWhiteSpace(staffId)) return ProfileFailure(MedicalProfileError.InvalidActor);
        if (await store.PatientAsync(id, ct) is null) return ProfileFailure(MedicalProfileError.PatientNotFound);
        var profile = await store.ProfileAsync(id, ct);
        if (profile is null && input.ExpectedRowVersion is { Length: > 0 })
            return ProfileFailure(MedicalProfileError.ProfileChanged);
        if (profile is not null && input.ExpectedRowVersion is not { Length: 8 })
            return ProfileFailure(MedicalProfileError.InvalidRowVersion);
        if (profile is not null && !profile.RowVersion.SequenceEqual(input.ExpectedRowVersion!))
            return ProfileFailure(MedicalProfileError.ProfileChanged);
        var isNew = profile is null;
        profile ??= new PatientMedicalProfile(id, staffId, clock.GetUtcNow());
        var now = clock.GetUtcNow();
        var supersededIds = input.SupersededEntryIds ?? [];
        var activeIds = profile.Allergies.Cast<PatientClinicalEntry>().Concat(profile.ChronicConditions)
            .Concat(profile.Medications).Concat(profile.Surgeries).Concat(profile.FamilyHistory)
            .Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
        if (supersededIds.Count > 500 || supersededIds.Distinct().Count() != supersededIds.Count ||
            supersededIds.Any(x => !activeIds.Contains(x)))
            return ProfileFailure(MedicalProfileError.InvalidEntryReference);
        try
        {
            Merge(profile.Allergies, input.Allergies, x => x.Id, x => x.Source, x => x.ReviewStatus,
                (e, x) => e.SameClinicalContent(x.Substance, x.Reaction, x.Severity, x.Source),
                x => profile.RecordAllergy(x.Substance, x.Reaction, x.Severity, x.Source, staffId, now), profile, staffId, now, supersededIds);
            Merge(profile.ChronicConditions, input.ChronicConditions, x => x.Id, x => x.Source, x => x.ReviewStatus,
                (e, x) => e.SameClinicalContent(x.ConditionName, x.Notes, x.Source),
                x => profile.RecordChronicCondition(x.ConditionName, x.Notes, x.Source, staffId, now), profile, staffId, now, supersededIds);
            Merge(profile.Medications, input.Medications, x => x.Id, x => x.Source, x => x.ReviewStatus,
                (e, x) => e.SameClinicalContent(x.MedicationName, x.Status, x.Source),
                x => profile.RecordMedication(x.MedicationName, x.Status, x.Source, staffId, now), profile, staffId, now, supersededIds);
            Merge(profile.Surgeries, input.Surgeries, x => x.Id, x => x.Source, x => x.ReviewStatus,
                (e, x) => e.SameClinicalContent(x.ProcedureName, x.PerformedOn, x.Source),
                x => profile.RecordSurgery(x.ProcedureName, x.PerformedOn, x.Source, staffId, now), profile, staffId, now, supersededIds);
            Merge(profile.FamilyHistory, input.FamilyHistory, x => x.Id, x => x.Source, x => x.ReviewStatus,
                (e, x) => e.SameClinicalContent(x.Relation, x.Condition, x.Source),
                x => profile.RecordFamilyHistory(x.Relation, x.Condition, x.Source, staffId, now), profile, staffId, now, supersededIds);
            if (profile.Allergies.Any(x => x.IsActive) && input.AllergyStatus != AllergyStatus.HasKnownAllergies ||
                !profile.Allergies.Any(x => x.IsActive) && input.AllergyStatus == AllergyStatus.HasKnownAllergies)
                return ProfileFailure(MedicalProfileError.InconsistentAllergyStatus);
            profile.UpdateBasics(input.BloodType, input.AllergyStatus, input.SmokingStatus, input.DiabetesType, staffId, now);
        }
        catch (EntryReviewException) { return ProfileFailure(MedicalProfileError.EntryCannotBeUnverified); }
        catch (ArgumentException) { return ProfileFailure(MedicalProfileError.InvalidEntryReference); }
        catch (InvalidOperationException) { return ProfileFailure(MedicalProfileError.InvalidEntryReference); }
        if (isNew) store.Add(profile);
        else store.ExpectVersion(profile, input.ExpectedRowVersion!);
        auditWriter.Append(new AuditAppendRequest(staffId, "patient.clinical-profile.update", "patient",
            id.ToString("N"), id, AuditOutcome.Succeeded, null,
            [new KeyValuePair<string, string>("entry-count",
                (profile.Allergies.Count(x => x.IsActive) + profile.ChronicConditions.Count(x => x.IsActive)
                 + profile.Medications.Count(x => x.IsActive) + profile.Surgeries.Count(x => x.IsActive)
                 + profile.FamilyHistory.Count(x => x.IsActive)).ToString())]));
        try { await store.SaveAsync(ct); }
        catch (PersistenceConcurrencyException) { return ProfileFailure(MedicalProfileError.ProfileChanged); }
        catch (PatientRecordConflictException) { return ProfileFailure(MedicalProfileError.ProfileChanged); }
        return MedicalProfileResult.Success(Profile(profile));
    }

    private PatientAdminResult AdminFailure(PatientAdminError error)
    {
        store.DiscardChanges();
        return PatientAdminResult.Failure(error);
    }
    private MedicalProfileResult ProfileFailure(MedicalProfileError error)
    {
        store.DiscardChanges();
        return MedicalProfileResult.Failure(error);
    }

    private sealed class EntryReviewException : Exception;
    private static void Merge<T, TInput>(IReadOnlyCollection<T> entries, IReadOnlyList<TInput> inputs,
        Func<TInput, Guid?> id, Func<TInput, MedicalRecordSource> source, Func<TInput, ClinicalReviewStatus> review,
        Func<T, TInput, bool> same, Func<TInput, T> create, PatientMedicalProfile profile, string staffId, DateTimeOffset now, IReadOnlyList<Guid> supersededIds)
        where T : PatientClinicalEntry
    {
        if (inputs is null || inputs.Count > 100 || inputs.Any(x => x is null)) throw new ArgumentException("Invalid entry list.");
        var active = entries.Where(x => x.IsActive).ToDictionary(x => x.Id);
        var ids = inputs.Where(x => id(x).HasValue).Select(x => id(x)!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length || ids.Any(x => !active.ContainsKey(x)))
            throw new ArgumentException("Entry does not belong to this profile.");
        if (ids.Any(supersededIds.Contains) || active.Keys.Any(x => !ids.Contains(x) && !supersededIds.Contains(x)))
            throw new ArgumentException("Incomplete snapshot or conflicting supersession.");
        foreach (var input in inputs)
        {
            if (!Enum.IsDefined(source(input)) || !Enum.IsDefined(review(input))) throw new ArgumentException("Invalid enum.");
            if (source(input) == MedicalRecordSource.Staff && review(input) != ClinicalReviewStatus.Verified) throw new EntryReviewException();
            T entry;
            if (id(input) is Guid key)
            {
                entry = active[key];
                if (entry.ReviewStatus == ClinicalReviewStatus.Verified &&
                    (review(input) != ClinicalReviewStatus.Verified ||
                     !same(entry, input) && source(input) == MedicalRecordSource.Patient))
                    throw new EntryReviewException();
                if (!same(entry, input))
                {
                    profile.SupersedeEntry(entry, staffId, now);
                    entry = create(input);
                }
            }
            else entry = create(input);
            if (review(input) == ClinicalReviewStatus.Verified && entry.ReviewStatus == ClinicalReviewStatus.PendingReview)
                profile.VerifyEntry(entry, staffId, now);
        }
        foreach (var entry in active.Values.Where(x => supersededIds.Contains(x.Id))) profile.SupersedeEntry(entry, staffId, now);
    }

    private PatientAdminError ValidateContact(string name, string phone, DateOnly? dateOfBirth,
        string? emergencyName, string? emergencyPhone, string? relation)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) return PatientAdminError.InvalidPatientName;
        if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length > 50) return PatientAdminError.InvalidPhoneNumber;
        if (dateOfBirth > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)) return PatientAdminError.InvalidDateOfBirth;
        var hasName = !string.IsNullOrWhiteSpace(emergencyName);
        var hasPhone = !string.IsNullOrWhiteSpace(emergencyPhone);
        if (hasName != hasPhone || !hasName && !string.IsNullOrWhiteSpace(relation) ||
            (emergencyName?.Length ?? 0) > 200 || (emergencyPhone?.Length ?? 0) > 50 || (relation?.Length ?? 0) > 100)
            return PatientAdminError.InvalidEmergencyContact;
        return PatientAdminError.None;
    }
    private static PatientAdminRecordDetails Admin(Patient p) => new(p.Id, p.MedicalRecordNumber, p.LegacyPaperFileNumber,
        p.LegacyCoverImageReference, p.FullName, p.PhoneNumber, p.DateOfBirth, p.EmergencyContactName,
        p.EmergencyContactPhone, p.EmergencyContactRelation, p.CreatedAtUtc, p.RowVersion);

    private static MedicalProfileDetails Profile(PatientMedicalProfile p) => new(p.PatientId, p.BloodType, p.AllergyStatus,
        p.SmokingStatus, p.DiabetesType, p.UpdatedAtUtc, p.RowVersion,
        p.Allergies.Where(x => x.IsActive).Select(x => new AllergyDetails(x.Id, x.Substance, x.Reaction, x.Severity, x.Source, x.ReviewStatus, x.RecordedAtUtc, x.VerifiedAtUtc)).ToArray(),
        p.ChronicConditions.Where(x => x.IsActive).Select(x => new ChronicConditionDetails(x.Id, x.ConditionName, x.Notes, x.Source, x.ReviewStatus, x.RecordedAtUtc, x.VerifiedAtUtc)).ToArray(),
        p.Medications.Where(x => x.IsActive).Select(x => new MedicationDetails(x.Id, x.MedicationName, x.Status, x.Source, x.ReviewStatus, x.RecordedAtUtc, x.VerifiedAtUtc)).ToArray(),
        p.Surgeries.Where(x => x.IsActive).Select(x => new SurgeryDetails(x.Id, x.ProcedureName, x.PerformedOn, x.Source, x.ReviewStatus, x.RecordedAtUtc, x.VerifiedAtUtc)).ToArray(),
        p.FamilyHistory.Where(x => x.IsActive).Select(x => new FamilyHistoryDetails(x.Id, x.Relation, x.Condition, x.Source, x.ReviewStatus, x.RecordedAtUtc, x.VerifiedAtUtc)).ToArray());
}
