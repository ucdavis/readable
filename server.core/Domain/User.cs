using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class User
{
    public long UserId { get; set; }

    public Guid EntraObjectId { get; set; }

    [StringLength(320)]
    public string? Email { get; set; }

    [StringLength(200)]
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public ICollection<FileRecord> Files { get; set; } = new List<FileRecord>();

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(x => x.UserId);

            entity.Property(x => x.EntraObjectId).IsRequired();
            entity.HasIndex(x => x.EntraObjectId).IsUnique();

            entity.Property(x => x.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");
        });
    }
}
