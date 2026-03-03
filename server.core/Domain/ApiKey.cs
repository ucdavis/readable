using Microsoft.EntityFrameworkCore;

namespace server.core.Domain;

public class ApiKey
{
    public int ApiKeyId { get; set; }

    public long UserId { get; set; }

    public User User { get; set; } = null!;

    /// <summary>PBKDF2 hash of the raw key (32 bytes, 100k iterations, SHA-256).</summary>
    public byte[] Hash { get; set; } = [];

    /// <summary>16-byte random salt used for the PBKDF2 hash.</summary>
    public byte[] Salt { get; set; } = [];

    /// <summary>HMACSHA256 of the raw key bytes using the server-side secret. Indexed for fast lookup.</summary>
    public byte[] Lookup { get; set; } = [];

    /// <summary>Last 4 characters of the Base64Url-encoded key (displayed in the UI for identification).</summary>
    public string KeyHint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    internal static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("ApiKeys");
            entity.HasKey(x => x.ApiKeyId);

            entity.Property(x => x.Hash).IsRequired();
            entity.Property(x => x.Salt).IsRequired();
            entity.Property(x => x.Lookup).IsRequired().HasMaxLength(900);
            entity.Property(x => x.KeyHint).IsRequired().HasMaxLength(10);

            entity.Property(x => x.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            // One key per user
            entity.HasIndex(x => x.UserId).IsUnique();

            // Fast lookup by HMAC tag
            entity.HasIndex(x => x.Lookup);

            entity.HasOne(x => x.User)
                .WithOne(u => u.ApiKey)
                .HasForeignKey<ApiKey>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
