using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnCallApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDutyHourCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DutyHourRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MaxHoursPerPeriod = table.Column<int>(type: "int", nullable: false),
                    PeriodDays = table.Column<int>(type: "int", nullable: false),
                    MinHoursBetweenShifts = table.Column<int>(type: "int", nullable: false),
                    MaxShiftLengthHours = table.Column<int>(type: "int", nullable: false),
                    MaxConsecutiveDays = table.Column<int>(type: "int", nullable: false),
                    ApplicableRoles = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyHourRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DutyHourRules_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DutyHourViolations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    ViolatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyHourViolations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DutyHourViolations_DutyHourRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "DutyHourRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DutyHourViolations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 16, 32, 15, 739, DateTimeKind.Utc).AddTicks(1629));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 16, 32, 15, 739, DateTimeKind.Utc).AddTicks(1634));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 16, 32, 15, 739, DateTimeKind.Utc).AddTicks(1636));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 16, 32, 15, 739, DateTimeKind.Utc).AddTicks(1638));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 16, 32, 15, 739, DateTimeKind.Utc).AddTicks(1639));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 16, 32, 15, 739, DateTimeKind.Utc).AddTicks(1641));

            migrationBuilder.CreateIndex(
                name: "IX_DutyHourRules_DepartmentId",
                table: "DutyHourRules",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyHourViolations_EmployeeId",
                table: "DutyHourViolations",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_DutyHourViolations_RuleId",
                table: "DutyHourViolations",
                column: "RuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DutyHourViolations");

            migrationBuilder.DropTable(
                name: "DutyHourRules");

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 15, 43, 22, 136, DateTimeKind.Utc).AddTicks(4999));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 15, 43, 22, 136, DateTimeKind.Utc).AddTicks(5005));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 15, 43, 22, 136, DateTimeKind.Utc).AddTicks(5007));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 15, 43, 22, 136, DateTimeKind.Utc).AddTicks(5008));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 15, 43, 22, 136, DateTimeKind.Utc).AddTicks(5010));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 20, 15, 43, 22, 136, DateTimeKind.Utc).AddTicks(5011));
        }
    }
}
