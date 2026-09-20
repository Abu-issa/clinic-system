using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotebookFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotebookPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PatientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorDoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinalizedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizedByDoctorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrentRevisionNumber = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookPages", x => x.Id);
                    table.CheckConstraint("CK_NotebookPages_RevisionNumber", "[CurrentRevisionNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_NotebookPages_Doctors_AuthorDoctorId",
                        column: x => x.AuthorDoctorId,
                        principalTable: "Doctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotebookPages_Doctors_FinalizedByDoctorId",
                        column: x => x.FinalizedByDoctorId,
                        principalTable: "Doctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotebookPages_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotebookPages_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotebookRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<long>(type: "bigint", nullable: false),
                    AuthorStaffId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ClientDraftId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OriginDeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotebookRevisions", x => x.Id);
                    table.CheckConstraint("CK_NotebookRevisions_RevisionNumber", "[RevisionNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_NotebookRevisions_AspNetUsers_AuthorStaffId",
                        column: x => x.AuthorStaffId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotebookRevisions_NotebookPages_PageId",
                        column: x => x.PageId,
                        principalTable: "NotebookPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookPages_AuthorDoctorId",
                table: "NotebookPages",
                column: "AuthorDoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookPages_FinalizedByDoctorId",
                table: "NotebookPages",
                column: "FinalizedByDoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookPages_PatientId_CreatedAtUtc",
                table: "NotebookPages",
                columns: new[] { "PatientId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookPages_PatientId_VisitId",
                table: "NotebookPages",
                columns: new[] { "PatientId", "VisitId" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookPages_VisitId",
                table: "NotebookPages",
                column: "VisitId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookRevisions_AuthorStaffId",
                table: "NotebookRevisions",
                column: "AuthorStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookRevisions_PageId_ClientDraftId",
                table: "NotebookRevisions",
                columns: new[] { "PageId", "ClientDraftId" },
                unique: true,
                filter: "[ClientDraftId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotebookRevisions_PageId_CreatedAtUtc",
                table: "NotebookRevisions",
                columns: new[] { "PageId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotebookRevisions_PageId_RevisionNumber",
                table: "NotebookRevisions",
                columns: new[] { "PageId", "RevisionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotebookRevisions");

            migrationBuilder.DropTable(
                name: "NotebookPages");
        }
    }
}
