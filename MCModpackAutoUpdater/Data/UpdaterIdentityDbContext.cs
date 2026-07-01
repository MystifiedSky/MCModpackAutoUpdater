using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MCModpackAutoUpdater.Data;

public sealed class UpdaterIdentityDbContext : IdentityDbContext<ApplicationUser>
{
    public UpdaterIdentityDbContext(DbContextOptions<UpdaterIdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<UpdaterAgentNode> UpdaterAgentNodes => Set<UpdaterAgentNode>();

    public DbSet<UpdaterAgentCommand> UpdaterAgentCommands => Set<UpdaterAgentCommand>();

    public DbSet<UpdaterModpackProfile> UpdaterModpackProfiles => Set<UpdaterModpackProfile>();

    public DbSet<UpdaterModpackUpdateAudit> UpdaterModpackUpdateAudits => Set<UpdaterModpackUpdateAudit>();

    public DbSet<UpdaterAmpControllerSettings> UpdaterAmpControllerSettings => Set<UpdaterAmpControllerSettings>();

    public DbSet<UpdaterDirectAmpApiSettings> UpdaterDirectAmpApiSettings => Set<UpdaterDirectAmpApiSettings>();

    public DbSet<UpdaterRuntimeSettings> UpdaterRuntimeSettings => Set<UpdaterRuntimeSettings>();

    public DbSet<UpdaterDiscordSettings> UpdaterDiscordSettings => Set<UpdaterDiscordSettings>();

    public DbSet<UpdaterDiscordAnnouncement> UpdaterDiscordAnnouncements => Set<UpdaterDiscordAnnouncement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<UpdaterAgentNode>(entity =>
        {
            entity.ToTable("UpdaterAgentNodes");
            entity.Property(agent => agent.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(agent => agent.Name).IsUnique();
            entity.Property(agent => agent.Host).HasMaxLength(255).IsRequired();
            entity.Property(agent => agent.ApiBaseUrl).HasMaxLength(500);
            entity.Property(agent => agent.Platform).HasMaxLength(50).IsRequired();
            entity.Property(agent => agent.ExecutionMode).HasMaxLength(20).IsRequired();
            entity.Property(agent => agent.AuthTokenHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(agent => agent.AuthTokenHash).IsUnique();
            entity.Property(agent => agent.LastReportedStatus).HasMaxLength(500);
            entity.Property(agent => agent.LastReportedVersion).HasMaxLength(100);
        });

        builder.Entity<UpdaterAgentCommand>(entity =>
        {
            entity.ToTable("UpdaterAgentCommands");
            entity.Property(command => command.CommandType).HasMaxLength(100).IsRequired();
            entity.Property(command => command.PayloadJson).IsRequired();
            entity.Property(command => command.Status).HasMaxLength(30).IsRequired();
            entity.Property(command => command.ResultSummary).HasMaxLength(500);
            entity.HasIndex(command => new { command.AgentNodeId, command.Status });
            entity.HasIndex(command => command.CreatedUtc);
            entity.HasOne(command => command.AgentNode)
                .WithMany(agent => agent.Commands)
                .HasForeignKey(command => command.AgentNodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UpdaterModpackProfile>(entity =>
        {
            entity.ToTable("UpdaterModpackProfiles");
            entity.HasIndex(profile => profile.AgentNodeId);
            entity.Property(profile => profile.Name).HasMaxLength(120).IsRequired();
            entity.HasIndex(profile => profile.Name).IsUnique();
            entity.Property(profile => profile.Provider).HasMaxLength(40).IsRequired();
            entity.Property(profile => profile.SourceReference).HasMaxLength(300);
            entity.Property(profile => profile.ServerPackUrl).HasMaxLength(500);
            entity.Property(profile => profile.ServerPackExcludedPaths).HasMaxLength(4000);
            entity.Property(profile => profile.ServerPackExcludedCurseForgeProjectIds).HasMaxLength(4000);
            entity.Property(profile => profile.VersionLock).HasMaxLength(100);
            entity.Property(profile => profile.CurrentVersion).HasMaxLength(100);
            entity.Property(profile => profile.CurrentVersionDisplay).HasMaxLength(100);
            entity.Property(profile => profile.InstallRootPath).HasMaxLength(500).IsRequired();
            entity.Property(profile => profile.OverrideDirectory).HasMaxLength(300);
            entity.Property(profile => profile.PreservedPaths).HasMaxLength(2000);
            entity.Property(profile => profile.ScheduleTime).HasMaxLength(5);
            entity.Property(profile => profile.RestartMode).HasMaxLength(30).IsRequired();
            entity.Property(profile => profile.AmpInstanceName).HasMaxLength(150);
            entity.Property(profile => profile.AmpApiUrl).HasMaxLength(500);
            entity.Property(profile => profile.DiscordAnnouncementChannelId).HasMaxLength(50);
            entity.Property(profile => profile.DiscordAnnouncementRoleId).HasMaxLength(50);
            entity.Property(profile => profile.RequestedVersion).HasMaxLength(100);
            entity.Property(profile => profile.LastScheduledCheckDate).HasMaxLength(10);
            entity.Property(profile => profile.LastDryRunCheckSummary).HasMaxLength(500);
            entity.Property(profile => profile.LastDryRunCheckTargetVersion).HasMaxLength(100);
            entity.Property(profile => profile.LastDryRunCheckTargetVersionDisplay).HasMaxLength(100);
            entity.Property(profile => profile.LastSummary).HasMaxLength(500);
            entity.HasOne(profile => profile.AgentNode)
                .WithMany(agent => agent.ModpackProfiles)
                .HasForeignKey(profile => profile.AgentNodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<UpdaterModpackUpdateAudit>(entity =>
        {
            entity.ToTable("UpdaterModpackUpdateAudits");
            entity.Property(audit => audit.TriggerSource).HasMaxLength(100).IsRequired();
            entity.Property(audit => audit.RequestedVersion).HasMaxLength(100);
            entity.Property(audit => audit.PreviousVersion).HasMaxLength(100);
            entity.Property(audit => audit.TargetVersion).HasMaxLength(100);
            entity.Property(audit => audit.TargetVersionDisplay).HasMaxLength(100);
            entity.Property(audit => audit.AppliedVersion).HasMaxLength(100);
            entity.Property(audit => audit.AppliedVersionDisplay).HasMaxLength(100);
            entity.Property(audit => audit.Status).HasMaxLength(30).IsRequired();
            entity.Property(audit => audit.Summary).HasMaxLength(500);
            entity.HasIndex(audit => new { audit.ModpackProfileId, audit.CreatedUtc });
            entity.HasIndex(audit => new { audit.Status, audit.CreatedUtc });
            entity.HasIndex(audit => audit.AgentCommandId).IsUnique();
            entity.HasOne(audit => audit.ModpackProfile)
                .WithMany(profile => profile.UpdateAudits)
                .HasForeignKey(audit => audit.ModpackProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(audit => audit.AgentNode)
                .WithMany(agent => agent.ModpackUpdateAudits)
                .HasForeignKey(audit => audit.AgentNodeId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(audit => audit.AgentCommand)
                .WithOne(command => command.ModpackUpdateAudit)
                .HasForeignKey<UpdaterModpackUpdateAudit>(audit => audit.AgentCommandId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<UpdaterAmpControllerSettings>(entity =>
        {
            entity.ToTable("UpdaterAmpControllerSettings");
            entity.Property(settings => settings.ControllerApiUrl).HasMaxLength(500);
            entity.Property(settings => settings.Username).HasMaxLength(200);
            entity.Property(settings => settings.Password).HasMaxLength(1000);
            entity.Property(settings => settings.Token).HasMaxLength(1000);
        });

        builder.Entity<UpdaterDirectAmpApiSettings>(entity =>
        {
            entity.ToTable("UpdaterDirectAmpApiSettings");
            entity.Property(settings => settings.Username).HasMaxLength(200);
            entity.Property(settings => settings.Password).HasMaxLength(1000);
            entity.Property(settings => settings.Token).HasMaxLength(1000);
            entity.Property(settings => settings.WarningMessageTemplate).HasMaxLength(500);
        });

        builder.Entity<UpdaterRuntimeSettings>(entity =>
        {
            entity.ToTable("UpdaterRuntimeSettings");
            entity.Property(settings => settings.ScheduleTimeZone).HasMaxLength(100).IsRequired();
        });

        builder.Entity<UpdaterDiscordSettings>(entity =>
        {
            entity.ToTable("UpdaterDiscordSettings");
            entity.Property(settings => settings.BotToken).HasMaxLength(1000);
            entity.Property(settings => settings.MessageTemplate).HasMaxLength(1000);
        });

        builder.Entity<UpdaterDiscordAnnouncement>(entity =>
        {
            entity.ToTable("UpdaterDiscordAnnouncements");
            entity.Property(announcement => announcement.ChannelId).HasMaxLength(50).IsRequired();
            entity.Property(announcement => announcement.RoleId).HasMaxLength(50);
            entity.Property(announcement => announcement.MessageContent).HasMaxLength(2000).IsRequired();
            entity.Property(announcement => announcement.Status).HasMaxLength(30).IsRequired();
            entity.Property(announcement => announcement.FailureReason).HasMaxLength(1000);
            entity.Property(announcement => announcement.DiscordMessageId).HasMaxLength(100);
            entity.HasIndex(announcement => new { announcement.Status, announcement.CreatedUtc });
            entity.HasOne(announcement => announcement.ModpackProfile)
                .WithMany()
                .HasForeignKey(announcement => announcement.ModpackProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(announcement => announcement.ModpackUpdateAudit)
                .WithMany()
                .HasForeignKey(announcement => announcement.ModpackUpdateAuditId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
