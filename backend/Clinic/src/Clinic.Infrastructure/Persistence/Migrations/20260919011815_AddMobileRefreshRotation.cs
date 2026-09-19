using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileRefreshRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefreshExpiresAtUtc",
                table: "MobileStaffSessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MobileStaffRefreshTokens",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileStaffRefreshTokens", x => x.TokenHash);
                    table.ForeignKey(
                        name: "FK_MobileStaffRefreshTokens_MobileStaffSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "MobileStaffSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MobileStaffRefreshTokens_SessionId",
                table: "MobileStaffRefreshTokens",
                column: "SessionId",
                unique: true,
                filter: "[ConsumedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MobileStaffRefreshTokens");

            migrationBuilder.DropColumn(
                name: "RefreshExpiresAtUtc",
                table: "MobileStaffSessions");
        }
    }
}
