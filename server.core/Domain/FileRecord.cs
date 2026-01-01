using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

/// <summary>
/// Calling it "FileRecord" to avoid confusion with System.IO.File and similar.
/// </summary>
public class FileRecord
{
    public static class Statuses
    {
        public const string Created = "Created";
        public const string Queued = "Queued";
        public const string Processing = "Processing";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";

        public static readonly string[] All =
        [
            Created,
            Queued,
            Processing,
            Completed,
            Failed,
            Cancelled,
        ];

        public static string CheckConstraintSql =>
            $"[Status] IN ('{string.Join("','", All)}')";
    }

    /// <summary>
    /// Will be used as primary id in blob storage as well.
    /// </summary>
    public Guid FileId { get; set; }

    public long OwnerUserId { get; set; }

    public User OwnerUser { get; set; } = default!;

    [StringLength(260)]
    public string OriginalFileName { get; set; } = string.Empty;

    [StringLength(128)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [StringLength(32)]
    public string Status { get; set; } = Statuses.Created;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset StatusUpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ICollection<FileProcessingAttempt> ProcessingAttempts { get; set; } = new List<FileProcessingAttempt>();

    public ICollection<AccessibilityReport> AccessibilityReports { get; set; } = new List<AccessibilityReport>();

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileRecord>(entity =>
        {
            entity.ToTable(
                "Files",
                table => table.HasCheckConstraint("CK_Files_Status", Statuses.CheckConstraintSql));
            entity.HasKey(x => x.FileId);

            entity.Property(x => x.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            entity.Property(x => x.StatusUpdatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            entity.Property(x => x.RowVersion)
                .IsRowVersion();

            entity.HasOne(x => x.OwnerUser)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => new { x.OwnerUserId, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.StatusUpdatedAt });
        });
    }
}
