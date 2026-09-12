using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Clinic.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentReschedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppointmentReschedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousStartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PreviousEndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NewStartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NewEndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ChangedByUserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentReschedules", x => x.Id);
                    table.CheckConstraint("CK_AppointmentReschedules_NewTimeRange", "[NewEndsAtUtc] > [NewStartsAtUtc]");
                    table.CheckConstraint("CK_AppointmentReschedules_PreviousTimeRange", "[PreviousEndsAtUtc] > [PreviousStartsAtUtc]");
                    table.ForeignKey(
                        name: "FK_AppointmentReschedules_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentReschedules_AppointmentId_ChangedAtUtc",
                table: "AppointmentReschedules",
                columns: new[] { "AppointmentId", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentReschedules");
        }
    }
}
