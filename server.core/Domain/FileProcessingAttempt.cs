using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class FileProcessingAttempt
{
    public static class Triggers
    {
        public const string Upload = "Upload";
        public const string Retry = "Retry";
        public const string Manual = "Manual";

        public static readonly string[] All =
        [
            Upload,
            Retry,
            Manual,
        ];

        public static string CheckConstraintSql =>
            $"[Trigger] IN ('{string.Join("','", All)}')";
    }

    public static class Outcomes
    {
        public const string Succeeded = "Succeeded";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";

        public static readonly string[] All =
        [
            Succeeded,
            Failed,
            Cancelled,
        ];

        public static string CheckConstraintSql =>
            $"[Outcome] IS NULL OR [Outcome] IN ('{string.Join("','", All)}')";
    }

    public long AttemptId { get; set; }

    public Guid FileId { get; set; }

    public FileRecord File { get; set; } = default!;

    public int AttemptNumber { get; set; }

    [StringLength(32)]
    public string Trigger { get; set; } = string.Empty;

    [StringLength(32)]
    public string? Outcome { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    [StringLength(128)]
    public string? ServiceBusMessageId { get; set; }

    public int? DeliveryCount { get; set; }

    public DateTimeOffset? EnqueuedTime { get; set; }

    public bool DeadLettered { get; set; }

    [StringLength(256)]
    public string? DeadLetterReason { get; set; }

    [StringLength(100)]
    public string? ErrorCode { get; set; }

    [StringLength(2048)]
    public string? ErrorMessage { get; set; }

    public string? ErrorDetails { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileProcessingAttempt>(entity =>
        {
            entity.ToTable(
                "FileProcessingAttempts",
                table =>
                {
                    table.HasCheckConstraint("CK_FileProcessingAttempts_Trigger", Triggers.CheckConstraintSql);
                    table.HasCheckConstraint("CK_FileProcessingAttempts_Outcome", Outcomes.CheckConstraintSql);
                });
            entity.HasKey(x => x.AttemptId);

            entity.Property(x => x.DeadLettered)
                .HasDefaultValue(false);

            entity.HasOne(x => x.File)
                .WithMany(x => x.ProcessingAttempts)
                .HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.FileId, x.AttemptNumber }).IsUnique();
        });
    }
}
