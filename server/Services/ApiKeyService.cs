using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using server.core.Data;
using server.core.Domain;

namespace Server.Services;

public interface IApiKeyService
{
    /// <summary>
    /// Generates a new API key for the given user, replacing any existing key.
    /// Returns the raw (unhashed) key — this is the only time it is available.
    /// </summary>
    Task<(string RawKey, string KeyHint, DateTimeOffset CreatedAt)> GenerateApiKeyAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Validates a raw API key string.
    /// Returns the UserId on success, or null if the key is invalid.
    /// </summary>
    Task<long?> ValidateApiKeyAsync(string rawKey, CancellationToken ct = default);

    /// <summary>
    /// Revokes the API key for the given user.
    /// </summary>
    Task RevokeApiKeyAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Returns summary info about the user's current API key, or null if none exists.
    /// </summary>
    Task<(string KeyHint, DateTimeOffset CreatedAt)?> GetApiKeyInfoAsync(long userId, CancellationToken ct = default);
}

public class ApiKeyService : IApiKeyService
{
    private const int SecretBytes = 32;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Pbkdf2Iterations = 100_000;

    private readonly AppDbContext _db;
    private readonly byte[] _hmacKey;

    public ApiKeyService(AppDbContext db, IConfiguration configuration)
    {
        _db = db;

        var secret = configuration["Auth:ApiKeyHmacSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Auth:ApiKeyHmacSecret is required. Set it in appsettings or as an environment variable.");
        }

        // Derive a fixed 32-byte key from the configured secret string
        _hmacKey = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    public async Task<(string RawKey, string KeyHint, DateTimeOffset CreatedAt)> GenerateApiKeyAsync(
        long userId, CancellationToken ct = default)
    {
        // 1. Generate raw secret
        var secretBytes = RandomNumberGenerator.GetBytes(SecretBytes);
        var rawKey = Base64UrlEncoder.Encode(secretBytes);

        // 2. Compute PBKDF2 hash with a fresh salt
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secretBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);

        // 3. Compute HMAC lookup tag for fast indexed DB queries
        var lookup = HMACSHA256.HashData(_hmacKey, secretBytes);

        // 4. Key hint — last 4 chars of the Base64Url-encoded key
        var keyHint = rawKey[^4..];

        var now = DateTimeOffset.UtcNow;

        // 5. Upsert: remove existing key (if any) then insert new one
        await _db.ApiKeys
            .Where(k => k.UserId == userId)
            .ExecuteDeleteAsync(ct);

        var apiKey = new ApiKey
        {
            UserId = userId,
            Hash = hash,
            Salt = salt,
            Lookup = lookup,
            KeyHint = keyHint,
            CreatedAt = now,
        };

        _db.ApiKeys.Add(apiKey);
        await _db.SaveChangesAsync(ct);

        return (rawKey, keyHint, now);
    }


    public async Task<long?> ValidateApiKeyAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length > 128)
        {
            return null;
        }

        byte[] secretBytes;
        try
        {
            secretBytes = Base64UrlEncoder.DecodeBytes(rawKey);
        }
        catch
        {
            return null; // malformed key
        }

        if (secretBytes.Length != SecretBytes)
        {
            return null;
        }

        // Fast indexed lookup by HMAC tag
        var lookup = HMACSHA256.HashData(_hmacKey, secretBytes);

        var apiKey = await _db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Lookup == lookup, ct);

        if (apiKey is null) return null;

        // Verify PBKDF2 hash
        var computedHash = Rfc2898DeriveBytes.Pbkdf2(
            secretBytes, apiKey.Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);

        return CryptographicOperations.FixedTimeEquals(computedHash, apiKey.Hash)
            ? apiKey.UserId
            : null;
    }

    public async Task RevokeApiKeyAsync(long userId, CancellationToken ct = default)
    {
        await _db.ApiKeys
            .Where(k => k.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<(string KeyHint, DateTimeOffset CreatedAt)?> GetApiKeyInfoAsync(long userId, CancellationToken ct = default)
    {
        var key = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.UserId == userId)
            .Select(k => new { k.KeyHint, k.CreatedAt })
            .FirstOrDefaultAsync(ct);

        return key is null ? null : (key.KeyHint, key.CreatedAt);
    }
}
