using Microsoft.EntityFrameworkCore;

namespace CmsEventService;

public sealed class Entity
{
    public required string Id { get; set; }
    public required string Payload { get; set; }
    public long Version { get; set; }
    public long TimestampTicks { get; set; }
    public bool Published { get; set; }
    public bool LocallyDisabled { get; set; }
}

// Contains no entity payload or payload fingerprint; retained across ID reuse.
public sealed class DeletionBarrier
{
    public required string Id { get; set; }
    public long TimestampTicks { get; set; }
}

public abstract class CmsContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Entity> Entities => Set<Entity>();
    public DbSet<DeletionBarrier> Deletions => Set<DeletionBarrier>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Entity>(e =>
        {
            e.ToTable("Entities", t => t.HasCheckConstraint("CK_Entity_Version", "\"Version\" > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128).UseCollation("C");
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Published, x.LocallyDisabled, x.Id });
        });
        model.Entity<DeletionBarrier>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128).UseCollation("C");
        });
    }
}

public sealed class WriterContext(DbContextOptions<WriterContext> options) : CmsContext(options);

public sealed class ReadContext : CmsContext
{
    public ReadContext(DbContextOptions<ReadContext> options) : base(options)
        => ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw new InvalidOperationException("Read context cannot write.");
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Read context cannot write.");
}

public static class EntityLock
{
    public static async Task Acquire(WriterContext db, IEnumerable<string> ids, CancellationToken ct)
    {
        // Database-wide transaction locks cover missing rows too. Sorted lock keys avoid deadlocks;
        // hash collisions only serialize unrelated IDs and cannot compromise correctness.
        var keys = ids.Select(id => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))))
            .Distinct().Order().ToArray();
        foreach (var key in keys)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }
}
