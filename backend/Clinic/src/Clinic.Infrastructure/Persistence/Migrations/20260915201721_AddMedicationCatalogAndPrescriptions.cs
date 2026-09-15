using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicationCatalogAndPrescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Medications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GenericNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GenericNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BrandNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BrandNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Strength = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Form = table.Column<int>(type: "int", nullable: false),
                    Route = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Medications", x => x.Id);
                    table.CheckConstraint("CK_Medications_StrengthToken", "CHARINDEX(' ', [Strength]) = 0");
                    table.CheckConstraint("CK_Medications_UnitToken", "CHARINDEX(' ', [Unit]) = 0");
                });

            migrationBuilder.CreateTable(
                name: "Prescriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReplacesPrescriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacedByPrescriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FinalizedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prescriptions", x => x.Id);
                    table.CheckConstraint("CK_Prescriptions_Lifecycle", "([Status] = 0 AND [FinalizedAtUtc] IS NULL AND [FinalizedByStaffId] IS NULL AND [ReleasedAtUtc] IS NULL AND [ReleasedByStaffId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancelledByStaffId] IS NULL) OR ([Status] = 1 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL AND [ReleasedAtUtc] IS NULL AND [ReleasedByStaffId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancelledByStaffId] IS NULL) OR ([Status] = 2 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL AND [ReleasedAtUtc] IS NOT NULL AND [ReleasedByStaffId] IS NOT NULL AND [CancelledAtUtc] IS NULL AND [CancelledByStaffId] IS NULL) OR ([Status] = 3 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancelledByStaffId] IS NOT NULL)");
                    table.CheckConstraint("CK_Prescriptions_ReplacedByOnlyWhenCancelled", "[ReplacedByPrescriptionId] IS NULL OR [Status] = 3");
                    table.ForeignKey(
                        name: "FK_Prescriptions_Doctors_DoctorId",
                        column: x => x.DoctorId,
                        principalTable: "Doctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Prescriptions_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Prescriptions_Prescriptions_ReplacedByPrescriptionId",
                        column: x => x.ReplacedByPrescriptionId,
                        principalTable: "Prescriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Prescriptions_Prescriptions_ReplacesPrescriptionId",
                        column: x => x.ReplacesPrescriptionId,
                        principalTable: "Prescriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Prescriptions_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrescriptionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrescriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MedicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GenericNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GenericNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BrandNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BrandNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Strength = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Form = table.Column<int>(type: "int", nullable: false),
                    Route = table.Column<int>(type: "int", nullable: false),
                    Dose = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Frequency = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Duration = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrescriptionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrescriptionItems_Medications_MedicationId",
                        column: x => x.MedicationId,
                        principalTable: "Medications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrescriptionItems_Prescriptions_PrescriptionId",
                        column: x => x.PrescriptionId,
                        principalTable: "Prescriptions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Medications_GenericNameAr_Strength_Unit_Form_Route",
                table: "Medications",
                columns: new[] { "GenericNameAr", "Strength", "Unit", "Form", "Route" },
                unique: true,
                filter: "[GenericNameAr] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Medications_GenericNameEn_Strength_Unit_Form_Route",
                table: "Medications",
                columns: new[] { "GenericNameEn", "Strength", "Unit", "Form", "Route" },
                unique: true,
                filter: "[GenericNameEn] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Medications_IsActive",
                table: "Medications",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_PrescriptionItems_MedicationId",
                table: "PrescriptionItems",
                column: "MedicationId");

            migrationBuilder.CreateIndex(
                name: "IX_PrescriptionItems_PrescriptionId_DisplayOrder",
                table: "PrescriptionItems",
                columns: new[] { "PrescriptionId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Prescriptions_DoctorId",
                table: "Prescriptions",
                column: "DoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_Prescriptions_PatientId_CreatedAtUtc",
                table: "Prescriptions",
                columns: new[] { "PatientId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Prescriptions_ReplacedByPrescriptionId",
                table: "Prescriptions",
                column: "ReplacedByPrescriptionId",
                unique: true,
                filter: "[ReplacedByPrescriptionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Prescriptions_ReplacesPrescriptionId",
                table: "Prescriptions",
                column: "ReplacesPrescriptionId",
                unique: true,
                filter: "[ReplacesPrescriptionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Prescriptions_VisitId",
                table: "Prescriptions",
                column: "VisitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrescriptionItems");

            migrationBuilder.DropTable(
                name: "Medications");

            migrationBuilder.DropTable(
                name: "Prescriptions");
        }
    }
}
