using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class AccessibilityReport
{
    public long ReportId { get; set; }

    public Guid FileId { get; set; }

    public FileRecord File { get; set; } = default!;

    [StringLength(16)]
    public string Stage { get; set; } = string.Empty;

    [StringLength(100)]
    public string Tool { get; set; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; set; }

    public int? IssueCount { get; set; }

    public string ReportJson { get; set; } = string.Empty;

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccessibilityReport>(entity =>
        {
            entity.ToTable("AccessibilityReports");
            entity.HasKey(x => x.ReportId);

            entity.Property(x => x.ReportJson)
                .IsRequired();

            entity.HasCheckConstraint(
                "CK_AccessibilityReports_Stage",
                "[Stage] IN ('Before','After')");

            entity.HasCheckConstraint(
                "CK_AccessibilityReports_ReportJson_IsJson",
                "ISJSON([ReportJson]) > 0");

            entity.HasOne(x => x.File)
                .WithMany(x => x.AccessibilityReports)
                .HasForeignKey(x => x.FileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.FileId, x.Stage, x.Tool }).IsUnique();
            entity.HasIndex(x => new { x.FileId, x.Stage, x.GeneratedAt });
        });
    }
}
