using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class ExternalApiRateLimitReservation
{
    public long ReservationId { get; set; }

    [StringLength(64)]
    public string Provider { get; set; } = string.Empty;

    [StringLength(64)]
    public string Operation { get; set; } = string.Empty;

    [StringLength(128)]
    public string BucketKey { get; set; } = string.Empty;

    [StringLength(64)]
    public string RequestId { get; set; } = string.Empty;

    public int Cost { get; set; }

    public DateTimeOffset ReservedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExternalApiRateLimitReservation>(entity =>
        {
            entity.ToTable(
                "ExternalApiRateLimitReservations",
                table => table.HasCheckConstraint("CK_ExternalApiRateLimitReservations_Cost", "[Cost] > 0"));
            entity.HasKey(x => x.ReservationId);

            entity.Property(x => x.Provider)
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(x => x.Operation)
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(x => x.BucketKey)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(x => x.RequestId)
                .HasMaxLength(64)
                .IsRequired();

            entity.HasIndex(x => x.RequestId)
                .IsUnique();

            entity.HasIndex(x => new { x.Provider, x.Operation, x.BucketKey, x.ExpiresAtUtc });
        });
    }
}
