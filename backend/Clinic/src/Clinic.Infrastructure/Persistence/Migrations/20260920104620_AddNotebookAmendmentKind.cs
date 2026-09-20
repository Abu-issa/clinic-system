using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotebookAmendmentKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_NotebookRevisions_Payload",
                table: "NotebookRevisions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotebookRevisions_Payload",
                table: "NotebookRevisions",
                sql: "([Kind] = 0 AND [StoredFileId] IS NULL) OR ([Kind] IN (1, 2) AND [StoredFileId] IS NOT NULL AND [ClientDraftId] IS NOT NULL AND [OriginDeviceId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_NotebookRevisions_Payload",
                table: "NotebookRevisions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotebookRevisions_Payload",
                table: "NotebookRevisions",
                sql: "([Kind] = 0 AND [StoredFileId] IS NULL) OR ([Kind] = 1 AND [StoredFileId] IS NOT NULL AND [ClientDraftId] IS NOT NULL AND [OriginDeviceId] IS NOT NULL)");
        }
    }
}
