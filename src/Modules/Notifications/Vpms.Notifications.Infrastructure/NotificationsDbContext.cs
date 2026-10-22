using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

public class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    public const string Schema = "notify";

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationCursor> Cursors => Set<NotificationCursor>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<NotificationTemplate> Templates => Set<NotificationTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("Notification");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            b.Property(x => x.SubjectLabel).HasMaxLength(300).IsRequired();
            b.Property(x => x.Path).HasMaxLength(300).IsRequired();
            b.Property(x => x.DataJson).IsRequired();
            b.Property(x => x.DedupeKey).HasMaxLength(200).IsRequired();
            // the same event is recorded once per person
            b.HasIndex(x => new { x.TenantId, x.UserId, x.DedupeKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAt, x.CreatedAt });
        });

        modelBuilder.Entity<NotificationCursor>(b =>
        {
            b.ToTable("NotificationCursor");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Source).HasMaxLength(60).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Source }).IsUnique();
        });

        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("NotificationPreference");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            b.Property(x => x.Channel).HasMaxLength(12).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.Kind, x.Channel }).IsUnique();
        });

        modelBuilder.Entity<NotificationTemplate>(b =>
        {
            b.ToTable("NotificationTemplate");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            b.Property(x => x.Channel).HasMaxLength(12).IsRequired();
            b.Property(x => x.Locale).HasMaxLength(5).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(TemplateEngine.MaxSubject).IsRequired();
            b.Property(x => x.Body).HasMaxLength(TemplateEngine.MaxBody).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Kind, x.Channel, x.Locale }).IsUnique();
        });
    }
}

internal sealed class DesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
