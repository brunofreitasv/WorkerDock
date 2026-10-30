using Microsoft.EntityFrameworkCore;

namespace WorkerDock.Infrastructure.Jobs;

internal sealed class JobDbContext(string connectionString) : DbContext
{
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<JobRevisionRow> Revisions => Set<JobRevisionRow>();
    public DbSet<CommandRow> Commands => Set<CommandRow>();

    protected override void OnConfiguring(DbContextOptionsBuilder builder) => builder.UseSqlServer(connectionString);
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<JobRow>(entity =>
        {
            entity.ToTable("Jobs", "workerdock");
            entity.HasKey(x => x.JobId);
            entity.Property(x => x.Version).IsRowVersion();
        });
        builder.Entity<JobRevisionRow>(entity =>
        {
            entity.ToTable("JobRevisions", "workerdock");
            entity.HasKey(x => new { x.JobId, x.Revision });
            entity.Property(x => x.DynamicValuesJson).HasColumnType("nvarchar(max)");
        });
        builder.Entity<CommandRow>(entity =>
        {
            entity.ToTable("Commands", "workerdock");
            entity.HasKey(x => x.CommandId);
        });
    }
}

internal sealed class JobRow
{
    public Guid JobId { get; set; }
    public long CurrentRevision { get; set; }
    public bool Enabled { get; set; }
    public bool Archived { get; set; }
    public int ConcurrencyLimit { get; set; }
    public bool PauseOnNeedsReview { get; set; }
    public byte[] Version { get; set; } = [];
}

internal sealed class JobRevisionRow
{
    public Guid JobId { get; set; }
    public long Revision { get; set; }
    public string Name { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateVersion { get; set; } = "";
    public string ConfigurationJson { get; set; } = "";
    public Guid? ProfileId { get; set; }
    public string PolicyJson { get; set; } = "";
    public string? DynamicValuesJson { get; set; }
    public bool HideFailureDetails { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class CommandRow
{
    public Guid CommandId { get; set; }
    public Guid TargetId { get; set; }
    public string CommandKind { get; set; } = "";
    public string State { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
