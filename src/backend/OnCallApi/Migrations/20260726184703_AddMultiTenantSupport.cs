using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OnCallApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenantSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Procedure",
                table: "PhoneTrees",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Employees",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Departments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Departments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "AuditLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "AppSettings",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CodeCallLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Zone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeCallLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodeCallLocations_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PhoneTreeEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhoneTreeId = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InitiatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocationZone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalIncidentId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResponseTimeSeconds = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DebriefNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhoneTreeEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhoneTreeEvents_Employees_InitiatedById",
                        column: x => x.InitiatedById,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PhoneTreeEvents_PhoneTrees_PhoneTreeId",
                        column: x => x.PhoneTreeId,
                        principalTable: "PhoneTrees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AzureAdGroupId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhoneTreeEventId = table.Column<int>(type: "int", nullable: false),
                    StepKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchSteps_PhoneTreeEvents_PhoneTreeEventId",
                        column: x => x.PhoneTreeEventId,
                        principalTable: "PhoneTreeEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhoneTreeEventParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhoneTreeEventId = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhoneTreeEventParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhoneTreeEventParticipants_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhoneTreeEventParticipants_PhoneTreeEvents_PhoneTreeEventId",
                        column: x => x.PhoneTreeEventId,
                        principalTable: "PhoneTreeEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantAdmins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AzureAdObjectId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsAutoAssigned = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAdmins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantAdmins_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CodeCallLocations",
                columns: new[] { "Id", "CreatedAt", "DepartmentId", "IsActive", "Name", "Zone" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1314), null, true, "3 West — Room 312", "3-west" },
                    { 2, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1317), null, true, "ICU — Bay 4", "icu" },
                    { 3, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1318), null, true, "Emergency Dept — Trauma 2", "ed" },
                    { 4, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1319), null, true, "Main Lobby", "lobby" },
                    { 5, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1320), null, true, "Radiology — MRI Suite", "radiology" },
                    { 6, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1321), null, true, "Labor & Delivery — Room 8", "ld" }
                });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Category", "CreatedAt", "TenantId" },
                values: new object[] { "Healthcare", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1837), 1 });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "Category", "CreatedAt", "TenantId" },
                values: new object[] { "Healthcare", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1843), 1 });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "Category", "CreatedAt", "TenantId" },
                values: new object[] { "Healthcare", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1846), 1 });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "Category", "CreatedAt", "TenantId" },
                values: new object[] { "Healthcare", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1847), 1 });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "Category", "CreatedAt", "TenantId" },
                values: new object[] { "Healthcare", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1849), 1 });

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "Category", "CreatedAt", "TenantId" },
                values: new object[] { "Healthcare", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1850), 1 });

            migrationBuilder.InsertData(
                table: "Departments",
                columns: new[] { "Id", "AzureAdGroupId", "Category", "CreatedAt", "Description", "IsActive", "Name", "TenantId" },
                values: new object[,]
                {
                    { 11, null, "Operations", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1858), "Facilities & Logistics", true, "Operations", null },
                    { 12, null, "Corporate", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1859), "Legal Affairs", true, "Legal & Compliance", null }
                });

            migrationBuilder.InsertData(
                table: "PhoneTrees",
                columns: new[] { "Id", "CreatedAt", "DepartmentId", "FallbackProcedure", "IsActive", "Name", "Procedure", "TreeType" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1356), null, null, true, "Code Blue — Cardiac Arrest", "Immediately call the code team and begin CPR. Bring crash cart to bedside. Assign team leads for airway, compressions, and medications.", "code-blue" },
                    { 2, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1360), null, null, true, "Code Red — Fire", "Evacuate immediate area. Close doors and windows. Activate fire alarm. Do not use elevators. Report to assembly point.", "code-red" },
                    { 3, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1362), null, null, true, "Code Green — Evacuation", "Begin horizontal evacuation to adjacent smoke compartment. Prepare for vertical evacuation if directed. Assist patients and visitors.", "code-green" },
                    { 4, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1363), null, null, true, "Code Silver — Active Threat", "Run. Hide. Fight. Lock all doors. Turn off lights. Stay quiet. Wait for law enforcement.", "code-silver" },
                    { 5, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1365), null, null, true, "Code Grey — Severe Weather", "Move patients away from windows. Close all blinds and curtains. Prepare for potential power outage.", "code-grey" },
                    { 6, new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1366), null, null, true, "Code Pink — Infant Abduction", "Secure all exits. Initiate lockdown. Check all persons leaving the unit. Notify security immediately.", "code-pink" }
                });

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "AzureAdGroupId", "ContactEmail", "CreatedAt", "Description", "IsActive", "Name" },
                values: new object[] { 1, null, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Primary hospital facility", true, "Main Hospital" });

            migrationBuilder.InsertData(
                table: "Departments",
                columns: new[] { "Id", "AzureAdGroupId", "Category", "CreatedAt", "Description", "IsActive", "Name", "TenantId" },
                values: new object[,]
                {
                    { 7, null, "Technology", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1852), "IT Infrastructure & Support", true, "Information Technology", 1 },
                    { 8, null, "Corporate", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1853), "People Operations", true, "Human Resources", 1 },
                    { 9, null, "Corporate", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1855), "Financial Services", true, "Finance & Accounting", 1 },
                    { 10, null, "Business", new DateTime(2026, 7, 26, 18, 47, 2, 659, DateTimeKind.Utc).AddTicks(1856), "Revenue & Growth", true, "Sales & Marketing", 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_TenantId",
                table: "Employees",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_TenantId",
                table: "Departments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_TenantId",
                table: "AppSettings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeCallLocations_DepartmentId",
                table: "CodeCallLocations",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchSteps_PhoneTreeEventId",
                table: "DispatchSteps",
                column: "PhoneTreeEventId");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneTreeEventParticipants_EmployeeId",
                table: "PhoneTreeEventParticipants",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneTreeEventParticipants_PhoneTreeEventId",
                table: "PhoneTreeEventParticipants",
                column: "PhoneTreeEventId");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneTreeEvents_InitiatedById",
                table: "PhoneTreeEvents",
                column: "InitiatedById");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneTreeEvents_PhoneTreeId",
                table: "PhoneTreeEvents",
                column: "PhoneTreeId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdmins_AzureAdObjectId",
                table: "TenantAdmins",
                column: "AzureAdObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdmins_TenantId_AzureAdObjectId",
                table: "TenantAdmins",
                columns: new[] { "TenantId", "AzureAdObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AppSettings_Tenants_TenantId",
                table: "AppSettings",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_Tenants_TenantId",
                table: "Departments",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Employees_Tenants_TenantId",
                table: "Employees",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppSettings_Tenants_TenantId",
                table: "AppSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_Departments_Tenants_TenantId",
                table: "Departments");

            migrationBuilder.DropForeignKey(
                name: "FK_Employees_Tenants_TenantId",
                table: "Employees");

            migrationBuilder.DropTable(
                name: "CodeCallLocations");

            migrationBuilder.DropTable(
                name: "DispatchSteps");

            migrationBuilder.DropTable(
                name: "PhoneTreeEventParticipants");

            migrationBuilder.DropTable(
                name: "TenantAdmins");

            migrationBuilder.DropTable(
                name: "PhoneTreeEvents");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Employees_TenantId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Departments_TenantId",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_AppSettings_TenantId",
                table: "AppSettings");

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "PhoneTrees",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "PhoneTrees",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "PhoneTrees",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "PhoneTrees",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "PhoneTrees",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "PhoneTrees",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DropColumn(
                name: "Procedure",
                table: "PhoneTrees");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppSettings");

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 21, 13, 55, 59, 802, DateTimeKind.Utc).AddTicks(3278));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 21, 13, 55, 59, 802, DateTimeKind.Utc).AddTicks(3284));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 21, 13, 55, 59, 802, DateTimeKind.Utc).AddTicks(3285));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 21, 13, 55, 59, 802, DateTimeKind.Utc).AddTicks(3287));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 21, 13, 55, 59, 802, DateTimeKind.Utc).AddTicks(3288));

            migrationBuilder.UpdateData(
                table: "Departments",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 7, 21, 13, 55, 59, 802, DateTimeKind.Utc).AddTicks(3289));
        }
    }
}
