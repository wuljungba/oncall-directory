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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Employee ──
        modelBuilder.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.AzureAdObjectId).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();

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

        // ── Seed Data ──
        modelBuilder.Entity<Department>().HasData(
            new Department { Id = 1, Name = "Emergency Medicine", Description = "Emergency Department" },
            new Department { Id = 2, Name = "Cardiology", Description = "Heart & Vascular" },
            new Department { Id = 3, Name = "Internal Medicine", Description = "General Medicine" },
            new Department { Id = 4, Name = "Pediatrics", Description = "Children's Health" },
            new Department { Id = 5, Name = "Surgery", Description = "Surgical Services" },
            new Department { Id = 6, Name = "Administration", Description = "Hospital Administration" }
        );
    }
}
