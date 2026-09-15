using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitsAndVitalMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Visits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChiefComplaint = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Symptoms = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Diagnosis = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    ClinicianNotes = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    InternalNotes = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    PatientSummary = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    SuggestedFollowUpAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FinalizedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visits", x => x.Id);
                    table.CheckConstraint("CK_Visits_Lifecycle", "([Status] = 0 AND [FinalizedAtUtc] IS NULL AND [FinalizedByStaffId] IS NULL) OR ([Status] = 1 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Visits_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Visits_Doctors_DoctorId",
                        column: x => x.DoctorId,
                        principalTable: "Doctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Visits_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisitAmendments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AmendmentText = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitAmendments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisitAmendments_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VitalMeasurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Unit = table.Column<int>(type: "int", nullable: false),
                    SystolicMmHg = table.Column<int>(type: "int", nullable: true),
                    DiastolicMmHg = table.Column<int>(type: "int", nullable: true),
                    HeartRateBpm = table.Column<int>(type: "int", nullable: true),
                    TemperatureCelsius = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: true),
                    OxygenSaturationPercent = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: true),
                    WeightKg = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: true),
                    HeightCm = table.Column<decimal>(type: "decimal(10,3)", precision: 10, scale: 3, nullable: true),
                    RespiratoryRateBreathsPerMin = table.Column<int>(type: "int", nullable: true),
                    MeasuredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VitalMeasurements", x => x.Id);
                    table.CheckConstraint("CK_VitalMeasurements_Shape", "([Type] = 0 AND [Unit] = 0 AND [SystolicMmHg] IS NOT NULL AND [DiastolicMmHg] IS NOT NULL AND [HeartRateBpm] IS NULL AND [TemperatureCelsius] IS NULL AND [OxygenSaturationPercent] IS NULL AND [WeightKg] IS NULL AND [HeightCm] IS NULL AND [RespiratoryRateBreathsPerMin] IS NULL) OR ([Type] = 1 AND [Unit] = 1 AND [SystolicMmHg] IS NULL AND [DiastolicMmHg] IS NULL AND [HeartRateBpm] IS NOT NULL AND [TemperatureCelsius] IS NULL AND [OxygenSaturationPercent] IS NULL AND [WeightKg] IS NULL AND [HeightCm] IS NULL AND [RespiratoryRateBreathsPerMin] IS NULL) OR ([Type] = 2 AND [Unit] = 2 AND [SystolicMmHg] IS NULL AND [DiastolicMmHg] IS NULL AND [HeartRateBpm] IS NULL AND [TemperatureCelsius] IS NOT NULL AND [OxygenSaturationPercent] IS NULL AND [WeightKg] IS NULL AND [HeightCm] IS NULL AND [RespiratoryRateBreathsPerMin] IS NULL) OR ([Type] = 3 AND [Unit] = 3 AND [SystolicMmHg] IS NULL AND [DiastolicMmHg] IS NULL AND [HeartRateBpm] IS NULL AND [TemperatureCelsius] IS NULL AND [OxygenSaturationPercent] IS NOT NULL AND [WeightKg] IS NULL AND [HeightCm] IS NULL AND [RespiratoryRateBreathsPerMin] IS NULL) OR ([Type] = 4 AND [Unit] = 4 AND [SystolicMmHg] IS NULL AND [DiastolicMmHg] IS NULL AND [HeartRateBpm] IS NULL AND [TemperatureCelsius] IS NULL AND [OxygenSaturationPercent] IS NULL AND [WeightKg] IS NOT NULL AND [HeightCm] IS NULL AND [RespiratoryRateBreathsPerMin] IS NULL) OR ([Type] = 5 AND [Unit] = 5 AND [SystolicMmHg] IS NULL AND [DiastolicMmHg] IS NULL AND [HeartRateBpm] IS NULL AND [TemperatureCelsius] IS NULL AND [OxygenSaturationPercent] IS NULL AND [WeightKg] IS NULL AND [HeightCm] IS NOT NULL AND [RespiratoryRateBreathsPerMin] IS NULL) OR ([Type] = 6 AND [Unit] = 6 AND [SystolicMmHg] IS NULL AND [DiastolicMmHg] IS NULL AND [HeartRateBpm] IS NULL AND [TemperatureCelsius] IS NULL AND [OxygenSaturationPercent] IS NULL AND [WeightKg] IS NULL AND [HeightCm] IS NULL AND [RespiratoryRateBreathsPerMin] IS NOT NULL)");
                    table.CheckConstraint("CK_VitalMeasurements_Values", "([SystolicMmHg] IS NULL OR [SystolicMmHg] >= 0) AND ([DiastolicMmHg] IS NULL OR [DiastolicMmHg] >= 0) AND ([HeartRateBpm] IS NULL OR [HeartRateBpm] >= 0) AND ([OxygenSaturationPercent] IS NULL OR [OxygenSaturationPercent] BETWEEN 0 AND 100) AND ([WeightKg] IS NULL OR [WeightKg] >= 0) AND ([HeightCm] IS NULL OR [HeightCm] >= 0) AND ([RespiratoryRateBreathsPerMin] IS NULL OR [RespiratoryRateBreathsPerMin] > 0)");
                    table.ForeignKey(
                        name: "FK_VitalMeasurements_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitAmendments_VisitId_CreatedAtUtc",
                table: "VisitAmendments",
                columns: new[] { "VisitId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Visits_AppointmentId",
                table: "Visits",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_DoctorId_OccurredAtUtc",
                table: "Visits",
                columns: new[] { "DoctorId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Visits_PatientId_OccurredAtUtc",
                table: "Visits",
                columns: new[] { "PatientId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VitalMeasurements_VisitId_MeasuredAtUtc",
                table: "VitalMeasurements",
                columns: new[] { "VisitId", "MeasuredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisitAmendments");

            migrationBuilder.DropTable(
                name: "VitalMeasurements");

            migrationBuilder.DropTable(
                name: "Visits");
        }
    }
}
