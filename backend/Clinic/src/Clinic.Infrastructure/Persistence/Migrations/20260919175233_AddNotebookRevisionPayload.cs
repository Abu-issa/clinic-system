using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotebookRevisionPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StoredFileId",
                table: "NotebookRevisions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotebookRevisions_StoredFileId",
                table: "NotebookRevisions",
                column: "StoredFileId",
                unique: true,
                filter: "[StoredFileId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotebookRevisions_Payload",
                table: "NotebookRevisions",
                sql: "([Kind] = 0 AND [StoredFileId] IS NULL) OR ([Kind] = 1 AND [StoredFileId] IS NOT NULL AND [ClientDraftId] IS NOT NULL AND [OriginDeviceId] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_NotebookRevisions_StoredFiles_StoredFileId",
                table: "NotebookRevisions",
                column: "StoredFileId",
                principalTable: "StoredFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotebookRevisions_StoredFiles_StoredFileId",
                table: "NotebookRevisions");

            migrationBuilder.DropIndex(
                name: "IX_NotebookRevisions_StoredFileId",
                table: "NotebookRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotebookRevisions_Payload",
                table: "NotebookRevisions");

            migrationBuilder.DropColumn(
                name: "StoredFileId",
                table: "NotebookRevisions");
        }
    }
}
