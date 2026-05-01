using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using server.core.Data;
using server.core.Domain;

namespace server.core.Ingest;

public interface IAdobePdfServicesRateLimiter
{
    Task WaitAsync(string operation, int cost, CancellationToken cancellationToken);

    Task RecordThrottleAsync(string operation, Exception exception, CancellationToken cancellationToken);
}

public static class AdobePdfServicesRateLimitOperations
{
    public const string AccessibilityChecker = "AccessibilityChecker";
}

public sealed class NoopAdobePdfServicesRateLimiter : IAdobePdfServicesRateLimiter
{
    public Task WaitAsync(string operation, int cost, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task RecordThrottleAsync(string operation, Exception exception, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class SqlAdobePdfServicesRateLimiter : IAdobePdfServicesRateLimiter
{
    private const string Provider = "AdobePdfServices";
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AdobePdfServicesRateLimitOptions _options;
    private readonly ILogger<SqlAdobePdfServicesRateLimiter> _logger;

    public SqlAdobePdfServicesRateLimiter(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IConfiguration configuration,
        ILogger<SqlAdobePdfServicesRateLimiter> logger)
    {
        _dbContextFactory = dbContextFactory;
        _options = AdobePdfServicesRateLimitOptions.FromConfiguration(configuration);
        _logger = logger;
    }

    public async Task WaitAsync(string operation, int cost, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || cost <= 0)
        {
            return;
        }

        if (cost > _options.RequestsPerWindow)
        {
            throw new InvalidOperationException(
                $"Adobe PDF Services rate limit cost {cost} exceeds configured capacity {_options.RequestsPerWindow}.");
        }

        var stopwatch = Stopwatch.StartNew();
        var loggedWait = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delay = await TryReserveAsync(operation, cost, cancellationToken);
            if (delay <= TimeSpan.Zero)
            {
                if (stopwatch.Elapsed >= _options.WaitLogThreshold)
                {
                    _logger.LogInformation(
                        "Adobe PDF Services rate limit wait completed for {operation} after {elapsedMs}ms",
                        operation,
                        stopwatch.Elapsed.TotalMilliseconds);
                }

                return;
            }

            if (!loggedWait && stopwatch.Elapsed + delay >= _options.WaitLogThreshold)
            {
                loggedWait = true;
                _logger.LogWarning(
                    "Adobe PDF Services rate limit delaying {operation} for {delayMs}ms; elapsedMs={elapsedMs}",
                    operation,
                    delay.TotalMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    public async Task RecordThrottleAsync(string operation, Exception exception, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _options.ThrottleCooldown <= TimeSpan.Zero)
        {
            return;
        }

        await using var strategyContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            await AcquireAppLockAsync(dbContext, operation, cancellationToken);

            var now = await GetDatabaseUtcNowAsync(dbContext, cancellationToken);
            var pausedUntil = now.Add(_options.ThrottleCooldown);
            var bucket = await GetOrAddBucketAsync(dbContext, operation, now, cancellationToken);
            if (bucket.PausedUntilUtc is null || bucket.PausedUntilUtc < pausedUntil)
            {
                bucket.PausedUntilUtc = pausedUntil;
                bucket.UpdatedAtUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogWarning(
                exception,
                "Adobe PDF Services rate limit cooldown recorded for {operation} until {pausedUntil:o}",
                operation,
                pausedUntil);
        });
    }

    private async Task<TimeSpan> TryReserveAsync(string operation, int cost, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await using var strategyContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            await AcquireAppLockAsync(dbContext, operation, cancellationToken);

            var now = await GetDatabaseUtcNowAsync(dbContext, cancellationToken);
            await DeleteExpiredReservationsAsync(dbContext, operation, now, cancellationToken);

            var bucket = await GetOrAddBucketAsync(dbContext, operation, now, cancellationToken);
            if (bucket.PausedUntilUtc is not null && bucket.PausedUntilUtc > now)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return bucket.PausedUntilUtc.Value - now;
            }

            var query = ActiveBucketReservations(dbContext, operation, now);
            var used = await query.SumAsync(x => (int?)x.Cost, cancellationToken) ?? 0;
            if (used + cost <= _options.RequestsPerWindow)
            {
                dbContext.ExternalApiRateLimitReservations.Add(new ExternalApiRateLimitReservation
                {
                    Provider = Provider,
                    Operation = operation,
                    BucketKey = _options.BucketKey,
                    RequestId = requestId,
                    Cost = cost,
                    ReservedAtUtc = now,
                    ExpiresAtUtc = now.Add(_options.Window),
                });
                bucket.UpdatedAtUtc = now;

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return TimeSpan.Zero;
            }

            var earliestExpiry = await query
                .OrderBy(x => x.ExpiresAtUtc)
                .Select(x => (DateTimeOffset?)x.ExpiresAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return earliestExpiry is null || earliestExpiry <= now
                ? TimeSpan.FromSeconds(1)
                : earliestExpiry.Value - now;
        });
    }

    private async Task<ExternalApiRateLimitBucket> GetOrAddBucketAsync(
        AppDbContext dbContext,
        string operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var bucket = await dbContext.ExternalApiRateLimitBuckets.SingleOrDefaultAsync(
            x => x.Provider == Provider &&
                 x.Operation == operation &&
                 x.BucketKey == _options.BucketKey,
            cancellationToken);

        if (bucket is not null)
        {
            return bucket;
        }

        bucket = new ExternalApiRateLimitBucket
        {
            Provider = Provider,
            Operation = operation,
            BucketKey = _options.BucketKey,
            UpdatedAtUtc = now,
        };
        dbContext.ExternalApiRateLimitBuckets.Add(bucket);
        return bucket;
    }

    private IQueryable<ExternalApiRateLimitReservation> CurrentBucketReservations(
        AppDbContext dbContext,
        string operation)
    {
        return dbContext.ExternalApiRateLimitReservations
            .Where(x => x.Provider == Provider &&
                        x.Operation == operation &&
                        x.BucketKey == _options.BucketKey);
    }

    private IQueryable<ExternalApiRateLimitReservation> ActiveBucketReservations(
        AppDbContext dbContext,
        string operation,
        DateTimeOffset now)
    {
        return CurrentBucketReservations(dbContext, operation)
            .Where(x => x.ExpiresAtUtc > now);
    }

    private async Task DeleteExpiredReservationsAsync(
        AppDbContext dbContext,
        string operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = await CurrentBucketReservations(dbContext, operation)
            .Where(x => x.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken);
        dbContext.ExternalApiRateLimitReservations.RemoveRange(expired);
    }

    private async Task AcquireAppLockAsync(
        AppDbContext dbContext,
        string operation,
        CancellationToken cancellationToken)
    {
        var resource = BuildAppLockResource(operation, _options.BucketKey);
        var lockTimeoutMs = (int)Math.Min(
            int.MaxValue,
            Math.Max(0, _options.SqlLockTimeout.TotalMilliseconds));

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult int;
            EXEC @lockResult = sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = {lockTimeoutMs};
            IF @lockResult < 0
            BEGIN
                THROW 51000, 'Failed to acquire Adobe PDF Services rate limit lock.', 1;
            END
            """, cancellationToken);
    }

    private static string BuildAppLockResource(string operation, string bucketKey)
    {
        var resource = $"{Provider}:{operation}:{bucketKey}";
        if (resource.Length <= 255)
        {
            return resource;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));
        return $"{Provider}:{operation}:{hash}";
    }

    private static async Task<DateTimeOffset> GetDatabaseUtcNowAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT SYSUTCDATETIME()";
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            _ => DateTimeOffset.UtcNow,
        };
    }

    public sealed record AdobePdfServicesRateLimitOptions(
        bool Enabled,
        int RequestsPerWindow,
        TimeSpan Window,
        TimeSpan ThrottleCooldown,
        string BucketKey,
        TimeSpan WaitLogThreshold,
        TimeSpan SqlLockTimeout)
    {
        public static AdobePdfServicesRateLimitOptions FromConfiguration(IConfiguration configuration)
        {
            var enabled =
                GetBool(configuration, "AdobePdfServices:RateLimit:Enabled", "ADOBE_PDF_SERVICES_RATE_LIMIT_ENABLED")
                ?? true;
            var requestsPerWindow = Math.Max(
                1,
                GetInt(
                    configuration,
                    "AdobePdfServices:RateLimit:RequestsPerMinute",
                    "ADOBE_PDF_SERVICES_RATE_LIMIT_REQUESTS_PER_MINUTE")
                ?? 20);
            var windowSeconds = Math.Max(
                1,
                GetDouble(
                    configuration,
                    "AdobePdfServices:RateLimit:WindowSeconds",
                    "ADOBE_PDF_SERVICES_RATE_LIMIT_WINDOW_SECONDS")
                ?? 60);
            var cooldownSeconds = Math.Max(
                0,
                GetDouble(
                    configuration,
                    "AdobePdfServices:RateLimit:ThrottleCooldownSeconds",
                    "ADOBE_PDF_SERVICES_RATE_LIMIT_THROTTLE_COOLDOWN_SECONDS")
                ?? 90);
            var waitLogThresholdSeconds = Math.Max(
                0,
                GetDouble(
                    configuration,
                    "AdobePdfServices:RateLimit:WaitLogThresholdSeconds",
                    "ADOBE_PDF_SERVICES_RATE_LIMIT_WAIT_LOG_THRESHOLD_SECONDS")
                ?? 5);
            var sqlLockTimeoutSeconds = Math.Max(
                1,
                GetDouble(
                    configuration,
                    "AdobePdfServices:RateLimit:SqlLockTimeoutSeconds",
                    "ADOBE_PDF_SERVICES_RATE_LIMIT_SQL_LOCK_TIMEOUT_SECONDS")
                ?? 15);

            var bucketKey =
                configuration["AdobePdfServices:RateLimit:BucketKey"]
                ?? configuration["AdobePdfServices__RateLimit__BucketKey"]
                ?? configuration["ADOBE_PDF_SERVICES_RATE_LIMIT_BUCKET_KEY"]
                ?? configuration["PDF_SERVICES_CLIENT_ID"]
                ?? configuration["AdobePdfServices:ClientId"]
                ?? configuration["AdobePdfServices__ClientId"]
                ?? "default";

            return new AdobePdfServicesRateLimitOptions(
                enabled,
                requestsPerWindow,
                TimeSpan.FromSeconds(windowSeconds),
                TimeSpan.FromSeconds(cooldownSeconds),
                Truncate(bucketKey.Trim(), 128),
                TimeSpan.FromSeconds(waitLogThresholdSeconds),
                TimeSpan.FromSeconds(sqlLockTimeoutSeconds));
        }

        private static bool? GetBool(IConfiguration configuration, string key, string legacyKey)
        {
            return configuration.GetValue<bool?>(key)
                   ?? configuration.GetValue<bool?>(key.Replace(":", "__", StringComparison.Ordinal))
                   ?? configuration.GetValue<bool?>(legacyKey);
        }

        private static int? GetInt(IConfiguration configuration, string key, string legacyKey)
        {
            return configuration.GetValue<int?>(key)
                   ?? configuration.GetValue<int?>(key.Replace(":", "__", StringComparison.Ordinal))
                   ?? configuration.GetValue<int?>(legacyKey);
        }

        private static double? GetDouble(IConfiguration configuration, string key, string legacyKey)
        {
            return configuration.GetValue<double?>(key)
                   ?? configuration.GetValue<double?>(key.Replace(":", "__", StringComparison.Ordinal))
                   ?? configuration.GetValue<double?>(legacyKey);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength];
        }
    }
}
