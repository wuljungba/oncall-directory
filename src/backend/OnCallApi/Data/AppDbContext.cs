using Microsoft.EntityFrameworkCore;
using OnCallApi.Models;

namespace OnCallApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ShiftSwap> ShiftSwaps => Set<ShiftSwap>();
    public DbSet<TimeOff> TimeOffs => Set<TimeOff>();
    public DbSet<PhoneTree> PhoneTrees => Set<PhoneTree>();
    public DbSet<PhoneTreeNode> PhoneTreeNodes => Set<PhoneTreeNode>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<DutyHourRule> DutyHourRules => Set<DutyHourRule>();
    public DbSet<DutyHourViolation> DutyHourViolations => Set<DutyHourViolation>();
    public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
    public DbSet<EscalationEvent> EscalationEvents => Set<EscalationEvent>();
    public DbSet<PhoneTreeEvent> PhoneTreeEvents => Set<PhoneTreeEvent>();
    public DbSet<PhoneTreeEventParticipant> PhoneTreeEventParticipants => Set<PhoneTreeEventParticipant>();
    public DbSet<DispatchStep> DispatchSteps => Set<DispatchStep>();
    public DbSet<DebriefNote> DebriefNotes => Set<DebriefNote>();
    public DbSet<CodeCallLocation> CodeCallLocations => Set<CodeCallLocation>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantAdmin> TenantAdmins => Set<TenantAdmin>();
    public DbSet<LocalAccount> LocalAccounts => Set<LocalAccount>();
    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();
    public DbSet<PublicShare> PublicShares => Set<PublicShare>();
    public DbSet<SignInIdentity> SignInIdentities => Set<SignInIdentity>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Employee ──
        modelBuilder.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.AzureAdObjectId).IsUnique().HasFilter("[AzureAdObjectId] IS NOT NULL AND [AzureAdObjectId] != ''");
            // FILTERED, because Email is now optional. An unfiltered unique index treats
            // every NULL as a value to be unique against on SQL Server, so the SECOND
            // email-less department contact ("3North", "4West") collides with the first
            // and the whole import rolls back. The filter keeps the real guarantee -- one
            // clinician cannot be imported twice under the same address -- while leaving
            // contacts that have no address alone.
            e.HasIndex(x => x.Email).IsUnique().HasFilter("[Email] IS NOT NULL");

            e.Property(x => x.Certifications).HasDefaultValue("[]");
            e.Property(x => x.Languages).HasDefaultValue("[]");
            e.Property(x => x.Presence).HasDefaultValue("unknown");

            e.HasOne(x => x.Department)
                .WithMany(d => d.Employees)
                .HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Manager)
                .WithMany(x => x.DirectReports)
                .HasForeignKey(x => x.ManagerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Schedule ──
        modelBuilder.Entity<Schedule>(s =>
        {
            s.HasOne(x => x.Department)
                .WithMany(d => d.Schedules)
                .HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Shift ──
        modelBuilder.Entity<Shift>(s =>
        {
            s.HasOne(x => x.Schedule)
                .WithMany(sch => sch.Shifts)
                .HasForeignKey(x => x.ScheduleId)
                .OnDelete(DeleteBehavior.Cascade);

            s.HasOne(x => x.Employee)
                .WithMany(e => e.Shifts)
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── ShiftSwap ──
        modelBuilder.Entity<ShiftSwap>(s =>
        {
            s.HasOne(x => x.OriginalShift)
                .WithMany()
                .HasForeignKey(x => x.OriginalShiftId)
                .OnDelete(DeleteBehavior.Restrict);

            s.HasOne(x => x.RequestedBy)
                .WithMany(e => e.SwapRequests)
                .HasForeignKey(x => x.RequestedById)
                .OnDelete(DeleteBehavior.Restrict);

            s.HasOne(x => x.ReplacementUser)
                .WithMany()
                .HasForeignKey(x => x.ReplacementUserId)
                .OnDelete(DeleteBehavior.Restrict);

            s.HasOne(x => x.ApprovedBy)
                .WithMany()
                .HasForeignKey(x => x.ApprovedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── TimeOff ──
        modelBuilder.Entity<TimeOff>(t =>
        {
            t.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            t.HasOne(x => x.ApprovedBy)
                .WithMany()
                .HasForeignKey(x => x.ApprovedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── PhoneTree ──
        modelBuilder.Entity<PhoneTree>(p =>
        {
            p.HasOne(x => x.Department)
                .WithMany()
                .HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PhoneTreeNode>(n =>
        {
            n.HasOne(x => x.PhoneTree)
                .WithMany(t => t.Nodes)
                .HasForeignKey(x => x.PhoneTreeId)
                .OnDelete(DeleteBehavior.Cascade);

            n.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── AuditLog ──
        modelBuilder.Entity<AuditLog>(a =>
        {
            a.HasIndex(x => x.Timestamp);
            a.HasIndex(x => new { x.ResourceType, x.ResourceId });
            a.Property(x => x.Action).HasMaxLength(50);
            a.Property(x => x.ResourceType).HasMaxLength(50);
        });

        // ── EscalationPolicy ──
        modelBuilder.Entity<EscalationPolicy>(e =>
        {
            e.HasOne(x => x.Department)
                .WithMany()
                .HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── EscalationEvent ──
        modelBuilder.Entity<EscalationEvent>(e =>
        {
            e.HasOne(x => x.Policy)
                .WithMany()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Shift)
                .WithMany()
                .HasForeignKey(x => x.ShiftId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── PhoneTreeEvent ──
        modelBuilder.Entity<PhoneTreeEvent>(e =>
        {
            e.HasOne(x => x.PhoneTree)
                .WithMany()
                .HasForeignKey(x => x.PhoneTreeId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.InitiatedBy)
                .WithMany()
                .HasForeignKey(x => x.InitiatedById)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasMany(x => x.Participants)
                .WithOne(p => p.PhoneTreeEvent)
                .HasForeignKey(p => p.PhoneTreeEventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── PhoneTreeEventParticipant ──
        modelBuilder.Entity<PhoneTreeEventParticipant>(p =>
        {
            p.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── DispatchStep ──
        modelBuilder.Entity<DispatchStep>(d =>
        {
            d.HasOne(x => x.PhoneTreeEvent)
                .WithMany(e => e.DispatchSteps)
                .HasForeignKey(x => x.PhoneTreeEventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── CodeCallLocation ──
        modelBuilder.Entity<CodeCallLocation>(l =>
        {
            l.HasOne(x => x.Department)
                .WithMany()
                .HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Tenant ──
        modelBuilder.Entity<Tenant>(t =>
        {
            t.HasIndex(x => x.Name).IsUnique();
        });

        // ── SignInIdentity ──
        // One row per principal per provider; the upsert in
        // IdentityDirectoryBackgroundService relies on this uniqueness.
        modelBuilder.Entity<SignInIdentity>(i =>
        {
            i.HasIndex(x => new { x.Provider, x.ExternalObjectId }).IsUnique();
            i.HasIndex(x => x.Email);
            i.HasIndex(x => x.LastSeenAt);
        });

        // ── TenantAdmin ──
        modelBuilder.Entity<TenantAdmin>(a =>
        {
            a.HasIndex(x => new { x.TenantId, x.AzureAdObjectId }).IsUnique();
            a.HasIndex(x => x.AzureAdObjectId);

            a.HasOne(x => x.Tenant)
                .WithMany(t => t.TenantAdmins)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Department ── (add Tenant relationship)
        modelBuilder.Entity<Department>(d =>
        {
            d.HasOne(x => x.Tenant)
                .WithMany(t => t.Departments)
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            d.HasIndex(x => x.TenantId);
        });

        // ── Employee ── (add Tenant relationship)
        modelBuilder.Entity<Employee>(e =>
        {
            e.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.TenantId);
        });

        // ── AppSetting ── (add Tenant relationship)
        modelBuilder.Entity<AppSetting>(s =>
        {
            s.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.SetNull);

            s.HasIndex(x => x.TenantId);
        });

        // ── LocalAccount ──
        modelBuilder.Entity<LocalAccount>(entity =>
        {
            entity.Ignore(x => x.Roles);
        });

        // ── PermissionGrant ──
        modelBuilder.Entity<PermissionGrant>(g =>
        {
            g.HasIndex(x => new { x.PrincipalType, x.ExternalPrincipalId });
            g.HasIndex(x => x.LocalUserId);

            g.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            g.HasOne(x => x.LocalUser)
                .WithMany()
                .HasForeignKey(x => x.LocalUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── PublicShare ──
        modelBuilder.Entity<PublicShare>(s =>
        {
            s.HasIndex(x => x.TenantId);
            s.HasIndex(x => x.Token).IsUnique();

            s.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Seed Data ──

        modelBuilder.Entity<Tenant>().HasData(
            new Tenant
            {
                Id = 1,
                Name = "Main Hospital",
                Description = "Primary hospital facility",
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );

        modelBuilder.Entity<CodeCallLocation>().HasData(
            new CodeCallLocation { Id = 1, Name = "3 West — Room 312", Zone = "3-west" },
            new CodeCallLocation { Id = 2, Name = "ICU — Bay 4", Zone = "icu" },
            new CodeCallLocation { Id = 3, Name = "Emergency Dept — Trauma 2", Zone = "ed" },
            new CodeCallLocation { Id = 4, Name = "Main Lobby", Zone = "lobby" },
            new CodeCallLocation { Id = 5, Name = "Radiology — MRI Suite", Zone = "radiology" },
            new CodeCallLocation { Id = 6, Name = "Labor & Delivery — Room 8", Zone = "ld" }
        );

        modelBuilder.Entity<PhoneTree>().HasData(
            new PhoneTree { Id = 1, Name = "Code Blue — Cardiac Arrest", TreeType = "code-blue", Procedure = "Immediately call the code team and begin CPR. Bring crash cart to bedside. Assign team leads for airway, compressions, and medications.", IsActive = true },
            new PhoneTree { Id = 2, Name = "Code Red — Fire", TreeType = "code-red", Procedure = "Evacuate immediate area. Close doors and windows. Activate fire alarm. Do not use elevators. Report to assembly point.", IsActive = true },
            new PhoneTree { Id = 3, Name = "Code Green — Evacuation", TreeType = "code-green", Procedure = "Begin horizontal evacuation to adjacent smoke compartment. Prepare for vertical evacuation if directed. Assist patients and visitors.", IsActive = true },
            new PhoneTree { Id = 4, Name = "Code Silver — Active Threat", TreeType = "code-silver", Procedure = "Run. Hide. Fight. Lock all doors. Turn off lights. Stay quiet. Wait for law enforcement.", IsActive = true },
            new PhoneTree { Id = 5, Name = "Code Grey — Severe Weather", TreeType = "code-grey", Procedure = "Move patients away from windows. Close all blinds and curtains. Prepare for potential power outage.", IsActive = true },
            new PhoneTree { Id = 6, Name = "Code Pink — Infant Abduction", TreeType = "code-pink", Procedure = "Secure all exits. Initiate lockdown. Check all persons leaving the unit. Notify security immediately.", IsActive = true }
        );

        modelBuilder.Entity<Department>().HasData(
            new Department { Id = 1, Name = "Emergency Medicine", Description = "Emergency Department", Category = "Healthcare", TenantId = 1 },
            new Department { Id = 2, Name = "Cardiology", Description = "Heart & Vascular", Category = "Healthcare", TenantId = 1 },
            new Department { Id = 3, Name = "Internal Medicine", Description = "General Medicine", Category = "Healthcare", TenantId = 1 },
            new Department { Id = 4, Name = "Pediatrics", Description = "Children's Health", Category = "Healthcare", TenantId = 1 },
            new Department { Id = 5, Name = "Surgery", Description = "Surgical Services", Category = "Healthcare", TenantId = 1 },
            new Department { Id = 6, Name = "Administration", Description = "Hospital Administration", Category = "Healthcare", TenantId = 1 },
            new Department { Id = 7, Name = "Information Technology", Description = "IT Infrastructure & Support", Category = "Technology", TenantId = 1 },
            new Department { Id = 8, Name = "Human Resources", Description = "People Operations", Category = "Corporate", TenantId = 1 },
            new Department { Id = 9, Name = "Finance & Accounting", Description = "Financial Services", Category = "Corporate", TenantId = 1 },
            new Department { Id = 10, Name = "Sales & Marketing", Description = "Revenue & Growth", Category = "Business", TenantId = 1 },
            new Department { Id = 11, Name = "Operations", Description = "Facilities & Logistics", Category = "Operations" },
            new Department { Id = 12, Name = "Legal & Compliance", Description = "Legal Affairs", Category = "Corporate" }
        );
    }
}
