using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicalTestResultsAndLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ClinicalTestRequests_Lifecycle",
                table: "ClinicalTestRequests");

            migrationBuilder.CreateTable(
                name: "ClinicalTestResultAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClinicalTestRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientAttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalTestResultAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinicalTestResultAttachments_ClinicalTestRequests_ClinicalTestRequestId",
                        column: x => x.ClinicalTestRequestId,
                        principalTable: "ClinicalTestRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicalTestResultAttachments_PatientAttachments_PatientAttachmentId",
                        column: x => x.PatientAttachmentId,
                        principalTable: "PatientAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClinicalTestRequests_Lifecycle",
                table: "ClinicalTestRequests",
                sql: "([Status] = 0 AND [UploadedAtUtc] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL) OR ([Status] IN (1, 2) AND [UploadedAtUtc] IS NOT NULL AND [RequestedAtUtc] <= [UploadedAtUtc] AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL) OR ([Status] = 3 AND [UploadedAtUtc] IS NOT NULL AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedByDoctorId] IS NOT NULL AND [RequestedAtUtc] <= [UploadedAtUtc] AND [UploadedAtUtc] <= [ReviewedAtUtc])");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalTestResultAttachments_ClinicalTestRequestId_LinkedAtUtc",
                table: "ClinicalTestResultAttachments",
                columns: new[] { "ClinicalTestRequestId", "LinkedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalTestResultAttachments_PatientAttachmentId",
                table: "ClinicalTestResultAttachments",
                column: "PatientAttachmentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicalTestResultAttachments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClinicalTestRequests_Lifecycle",
                table: "ClinicalTestRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClinicalTestRequests_Lifecycle",
                table: "ClinicalTestRequests",
                sql: "[Status] = 0 AND [UploadedAtUtc] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL");
        }
    }
}
