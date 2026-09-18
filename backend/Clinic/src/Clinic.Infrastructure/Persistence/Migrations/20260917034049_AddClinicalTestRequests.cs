using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicalTestRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClinicalTestRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedByDoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    TestName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClinicalInstructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedByDoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalTestRequests", x => x.Id);
                    table.CheckConstraint("CK_ClinicalTestRequests_Category", "[Category] IN (0, 1)");
                    table.CheckConstraint("CK_ClinicalTestRequests_Lifecycle", "[Status] = 0 AND [UploadedAtUtc] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL");
                    table.ForeignKey(
                        name: "FK_ClinicalTestRequests_Doctors_RequestedByDoctorId",
                        column: x => x.RequestedByDoctorId,
                        principalTable: "Doctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicalTestRequests_Doctors_ReviewedByDoctorId",
                        column: x => x.ReviewedByDoctorId,
                        principalTable: "Doctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicalTestRequests_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicalTestRequests_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalTestRequests_PatientId_RequestedAtUtc",
                table: "ClinicalTestRequests",
                columns: new[] { "PatientId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalTestRequests_RequestedByDoctorId",
                table: "ClinicalTestRequests",
                column: "RequestedByDoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalTestRequests_ReviewedByDoctorId",
                table: "ClinicalTestRequests",
                column: "ReviewedByDoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalTestRequests_VisitId_RequestedAtUtc",
                table: "ClinicalTestRequests",
                columns: new[] { "VisitId", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicalTestRequests");
        }
    }
}
