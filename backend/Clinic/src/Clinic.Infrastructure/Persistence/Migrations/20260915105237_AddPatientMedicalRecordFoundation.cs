using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientMedicalRecordFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactName",
                table: "Patients",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactPhone",
                table: "Patients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactRelation",
                table: "Patients",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyCoverImageReference",
                table: "Patients",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyPaperFileNumber",
                table: "Patients",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MedicalRecordNumber",
                table: "Patients",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Patients",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "PatientMedicalProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BloodType = table.Column<int>(type: "int", nullable: true),
                    AllergyStatus = table.Column<int>(type: "int", nullable: false),
                    SmokingStatus = table.Column<int>(type: "int", nullable: true),
                    DiabetesType = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientMedicalProfiles", x => x.Id);
                    table.CheckConstraint("CK_Profile_AllergyStatus", "[AllergyStatus] IN (0,1,2)");
                    table.CheckConstraint("CK_Profile_BloodType", "[BloodType] IS NULL OR [BloodType] BETWEEN 1 AND 8");
                    table.CheckConstraint("CK_Profile_DiabetesType", "[DiabetesType] IS NULL OR [DiabetesType] BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_Profile_SmokingStatus", "[SmokingStatus] IS NULL OR [SmokingStatus] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_PatientMedicalProfiles_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientAllergies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Substance = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reaction = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Severity = table.Column<int>(type: "int", nullable: true),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientAllergies", x => x.Id);
                    table.CheckConstraint("CK_Allergy_Severity", "[Severity] IS NULL OR [Severity] IN (1,2,3)");
                    table.CheckConstraint("CK_PatientAllergies_Review", "[ReviewStatus] IN (1,2)");
                    table.CheckConstraint("CK_PatientAllergies_Source", "[Source] IN (1,2)");
                    table.CheckConstraint("CK_PatientAllergies_Supersession", "([SupersededAtUtc] IS NULL AND [SupersededByStaffId] IS NULL) OR ([SupersededAtUtc] IS NOT NULL AND [SupersededByStaffId] IS NOT NULL)");
                    table.CheckConstraint("CK_PatientAllergies_Verification", "([ReviewStatus] = 1 AND [VerifiedAtUtc] IS NULL AND [VerifiedByStaffId] IS NULL) OR ([ReviewStatus] = 2 AND [VerifiedAtUtc] IS NOT NULL AND [VerifiedByStaffId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PatientAllergies_PatientMedicalProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PatientMedicalProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientChronicConditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConditionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientChronicConditions", x => x.Id);
                    table.CheckConstraint("CK_PatientChronicConditions_Review", "[ReviewStatus] IN (1,2)");
                    table.CheckConstraint("CK_PatientChronicConditions_Source", "[Source] IN (1,2)");
                    table.CheckConstraint("CK_PatientChronicConditions_Supersession", "([SupersededAtUtc] IS NULL AND [SupersededByStaffId] IS NULL) OR ([SupersededAtUtc] IS NOT NULL AND [SupersededByStaffId] IS NOT NULL)");
                    table.CheckConstraint("CK_PatientChronicConditions_Verification", "([ReviewStatus] = 1 AND [VerifiedAtUtc] IS NULL AND [VerifiedByStaffId] IS NULL) OR ([ReviewStatus] = 2 AND [VerifiedAtUtc] IS NOT NULL AND [VerifiedByStaffId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PatientChronicConditions_PatientMedicalProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PatientMedicalProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientFamilyHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Relation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Condition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientFamilyHistoryEntries", x => x.Id);
                    table.CheckConstraint("CK_PatientFamilyHistoryEntries_Review", "[ReviewStatus] IN (1,2)");
                    table.CheckConstraint("CK_PatientFamilyHistoryEntries_Source", "[Source] IN (1,2)");
                    table.CheckConstraint("CK_PatientFamilyHistoryEntries_Supersession", "([SupersededAtUtc] IS NULL AND [SupersededByStaffId] IS NULL) OR ([SupersededAtUtc] IS NOT NULL AND [SupersededByStaffId] IS NOT NULL)");
                    table.CheckConstraint("CK_PatientFamilyHistoryEntries_Verification", "([ReviewStatus] = 1 AND [VerifiedAtUtc] IS NULL AND [VerifiedByStaffId] IS NULL) OR ([ReviewStatus] = 2 AND [VerifiedAtUtc] IS NOT NULL AND [VerifiedByStaffId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PatientFamilyHistoryEntries_PatientMedicalProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PatientMedicalProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientMedications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MedicationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientMedications", x => x.Id);
                    table.CheckConstraint("CK_Medication_Status", "[Status] IN (1,2)");
                    table.CheckConstraint("CK_PatientMedications_Review", "[ReviewStatus] IN (1,2)");
                    table.CheckConstraint("CK_PatientMedications_Source", "[Source] IN (1,2)");
                    table.CheckConstraint("CK_PatientMedications_Supersession", "([SupersededAtUtc] IS NULL AND [SupersededByStaffId] IS NULL) OR ([SupersededAtUtc] IS NOT NULL AND [SupersededByStaffId] IS NOT NULL)");
                    table.CheckConstraint("CK_PatientMedications_Verification", "([ReviewStatus] = 1 AND [VerifiedAtUtc] IS NULL AND [VerifiedByStaffId] IS NULL) OR ([ReviewStatus] = 2 AND [VerifiedAtUtc] IS NOT NULL AND [VerifiedByStaffId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PatientMedications_PatientMedicalProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PatientMedicalProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientSurgeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcedureName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SupersededByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientSurgeries", x => x.Id);
                    table.CheckConstraint("CK_PatientSurgeries_Review", "[ReviewStatus] IN (1,2)");
                    table.CheckConstraint("CK_PatientSurgeries_Source", "[Source] IN (1,2)");
                    table.CheckConstraint("CK_PatientSurgeries_Supersession", "([SupersededAtUtc] IS NULL AND [SupersededByStaffId] IS NULL) OR ([SupersededAtUtc] IS NOT NULL AND [SupersededByStaffId] IS NOT NULL)");
                    table.CheckConstraint("CK_PatientSurgeries_Verification", "([ReviewStatus] = 1 AND [VerifiedAtUtc] IS NULL AND [VerifiedByStaffId] IS NULL) OR ([ReviewStatus] = 2 AND [VerifiedAtUtc] IS NOT NULL AND [VerifiedByStaffId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PatientSurgeries_PatientMedicalProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "PatientMedicalProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Patients_MedicalRecordNumber",
                table: "Patients",
                column: "MedicalRecordNumber",
                unique: true,
                filter: "[MedicalRecordNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientAllergies_ProfileId_SupersededAtUtc",
                table: "PatientAllergies",
                columns: new[] { "ProfileId", "SupersededAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientChronicConditions_ProfileId_SupersededAtUtc",
                table: "PatientChronicConditions",
                columns: new[] { "ProfileId", "SupersededAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientFamilyHistoryEntries_ProfileId_SupersededAtUtc",
                table: "PatientFamilyHistoryEntries",
                columns: new[] { "ProfileId", "SupersededAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientMedicalProfiles_PatientId",
                table: "PatientMedicalProfiles",
                column: "PatientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientMedications_ProfileId_SupersededAtUtc",
                table: "PatientMedications",
                columns: new[] { "ProfileId", "SupersededAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientSurgeries_ProfileId_SupersededAtUtc",
                table: "PatientSurgeries",
                columns: new[] { "ProfileId", "SupersededAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientAllergies");

            migrationBuilder.DropTable(
                name: "PatientChronicConditions");

            migrationBuilder.DropTable(
                name: "PatientFamilyHistoryEntries");

            migrationBuilder.DropTable(
                name: "PatientMedications");

            migrationBuilder.DropTable(
                name: "PatientSurgeries");

            migrationBuilder.DropTable(
                name: "PatientMedicalProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Patients_MedicalRecordNumber",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "EmergencyContactName",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "EmergencyContactPhone",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "EmergencyContactRelation",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "LegacyCoverImageReference",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "LegacyPaperFileNumber",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "MedicalRecordNumber",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Patients");
        }
    }
}
