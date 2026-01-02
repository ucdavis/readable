using System;
using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class UserRole
{
    public long UserId { get; set; }

    public User User { get; set; } = default!;

    public int RoleId { get; set; }

    public Role Role { get; set; } = default!;

    public DateTimeOffset AssignedAt { get; set; }

    public long? AssignedByUserId { get; set; }

    public User? AssignedByUser { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("UserRoles");
            entity.HasKey(x => new { x.UserId, x.RoleId });

            entity.Property(x => x.AssignedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            entity.HasOne(x => x.User)
                .WithMany(x => x.UserRoles)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Role)
                .WithMany(x => x.UserRoles)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.AssignedByUser)
                .WithMany()
                .HasForeignKey(x => x.AssignedByUserId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
