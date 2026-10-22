using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

public class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    public const string Schema = "wf";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<WorkflowDefinition> Definitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<ApprovalTask> Tasks => Set<ApprovalTask>();
    public DbSet<TaskDecision> Decisions => Set<TaskDecision>();
    public DbSet<JoinArrival> Joins => Set<JoinArrival>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<MaterialityPolicy> MaterialityPolicies => Set<MaterialityPolicy>();
    public DbSet<WorkflowNotice> Notices => Set<WorkflowNotice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WorkflowDefinition>(b =>
        {
            b.ToTable("WorkflowDefinition");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SubjectType).HasMaxLength(30).IsRequired();
            b.Property(x => x.SpecJson).IsRequired();
            b.Property(x => x.SpecHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.Source).HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500).IsRequired();
            b.Property(x => x.DecisionComment).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.SubjectType, x.VersionNo }).IsUnique();
            // At most one version in force and one waiting for approval per legal entity and subject type.
            b.HasIndex(x => new { x.LegalEntityId, x.SubjectType }).IsUnique().HasFilter("[Status] = 'Active'").HasDatabaseName("UX_WorkflowDefinition_Active");
            b.HasIndex(x => new { x.LegalEntityId, x.SubjectType }).IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("UX_WorkflowDefinition_Pending");
        });

        modelBuilder.Entity<WorkflowRun>(b =>
        {
            b.ToTable("WorkflowRun");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SubjectType).HasMaxLength(30).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.StatusReason).HasMaxLength(1000);
            b.Property(x => x.DefinitionHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.SubjectLabel).HasMaxLength(200).IsRequired();
            b.Property(x => x.RowVersion).IsRowVersion();
            b.Ignore(x => x.PendingTasks);
            b.Ignore(x => x.NeedsAttention);
            b.Property(x => x.Makers).HasConversion(GuidList, GuidListComparer());
            b.Property(x => x.Facts).HasColumnName("FactsJson").HasConversion(
                f => JsonSerializer.Serialize(f, Json), v => JsonSerializer.Deserialize<SubjectFacts>(v, Json)!);
            b.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Joins).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.SubjectType, x.SubjectId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Status });
            // One active run per subject.
            b.HasIndex(x => new { x.SubjectType, x.SubjectId }).IsUnique().HasFilter("[Status] = 'Active'").HasDatabaseName("UX_WorkflowRun_ActiveSubject");
        });

        modelBuilder.Entity<ApprovalTask>(b =>
        {
            b.ToTable("ApprovalTask");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.StepId).HasMaxLength(40).IsRequired();
            b.Property(x => x.BranchPath).HasMaxLength(400).IsRequired();
            b.Property(x => x.Mode).HasMaxLength(10).IsRequired();
            b.Property(x => x.Status).HasMaxLength(12).IsRequired();
            b.Property(x => x.AssigneeList).IsRequired();
            b.Ignore(x => x.Assignees);
            b.Ignore(x => x.Approvals);
            b.Ignore(x => x.EscalatedTo);
            b.Property(x => x.EscalatedToList).IsRequired();
            b.Ignore(x => x.IsSatisfied);
            b.HasMany(x => x.Decisions).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.RunId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.Status, x.DueAt });
        });

        modelBuilder.Entity<TaskDecision>(b =>
        {
            b.ToTable("TaskDecision");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Decision).HasMaxLength(10).IsRequired();
            b.Property(x => x.Comment).HasMaxLength(1000);
            b.HasIndex(x => new { x.TaskId, x.ActorId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ActorId });
        });

        modelBuilder.Entity<JoinArrival>(b =>
        {
            b.ToTable("JoinArrival");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Key).HasMaxLength(400).IsRequired();
            b.HasIndex(x => new { x.RunId, x.Key }).IsUnique();
        });

        modelBuilder.Entity<Delegation>(b =>
        {
            b.ToTable("Delegation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MaxAmount).HasPrecision(19, 4);
            b.Property(x => x.Status).HasMaxLength(10).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(300).IsRequired();
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.DelegateId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.DelegatorId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId });
        });

        modelBuilder.Entity<MaterialityPolicy>(b =>
        {
            b.ToTable("MaterialityPolicy");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SubjectType).HasMaxLength(30).IsRequired();
            b.Property(x => x.ConfigJson).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500).IsRequired();
            b.Property(x => x.DecisionComment).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.SubjectType, x.VersionNo }).IsUnique();
            b.HasIndex(x => new { x.LegalEntityId, x.SubjectType }).IsUnique().HasFilter("[Status] = 'Active'").HasDatabaseName("UX_MaterialityPolicy_Active");
            b.HasIndex(x => new { x.LegalEntityId, x.SubjectType }).IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("UX_MaterialityPolicy_Pending");
        });

        modelBuilder.Entity<WorkflowNotice>(b =>
        {
            b.ToTable("WorkflowNotice");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            b.Property(x => x.SubjectLabel).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAt });
            b.HasIndex(x => new { x.TaskId, x.UserId, x.Kind }).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<Guid>, string> GuidList = new(
        l => string.Join(',', l), v => v.Length == 0 ? new List<Guid>() : v.Split(',', StringSplitOptions.None).Select(Guid.Parse).ToList());

    private static ValueComparer<List<Guid>> GuidListComparer() =>
        new((a, b) => a!.SequenceEqual(b!), l => l.Aggregate(0, (h, g) => HashCode.Combine(h, g)), l => l.ToList());
}
