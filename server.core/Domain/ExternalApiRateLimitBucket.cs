using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class ExternalApiRateLimitBucket
{
    [StringLength(64)]
    public string Provider { get; set; } = string.Empty;

    [StringLength(64)]
    public string Operation { get; set; } = string.Empty;

    [StringLength(128)]
    public string BucketKey { get; set; } = string.Empty;

    public DateTimeOffset? PausedUntilUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExternalApiRateLimitBucket>(entity =>
        {
            entity.ToTable("ExternalApiRateLimitBuckets");
            entity.HasKey(x => new { x.Provider, x.Operation, x.BucketKey });

            entity.Property(x => x.Provider)
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(x => x.Operation)
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(x => x.BucketKey)
                .HasMaxLength(128)
                .IsRequired();

            entity.HasIndex(x => x.PausedUntilUtc);
        });
    }
}
