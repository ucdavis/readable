using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using server.Helpers;
using server.core.Data;
using server.core.Domain;

namespace Server.Services;

public interface IUserService
{
    /// <summary>
    /// Ensures the authenticated user exists in the database and syncs basic profile fields (name/email).
    /// </summary>
    Task<long?> SyncUserAsync(ClaimsPrincipal principal);

    Task<List<string>> GetRolesForUser(ClaimsPrincipal principal);

    Task<ClaimsPrincipal?> UpdateUserPrincipalIfNeeded(ClaimsPrincipal principal);
}

public class UserService : IUserService
{
    private readonly ILogger<UserService> _logger;
    private readonly AppDbContext _dbContext;

    public UserService(ILogger<UserService> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<long?> SyncUserAsync(ClaimsPrincipal principal)
    {
        // all of our users must have an Entra object id
        // if we ever changes this we should update how we pull the user out of the users table from the principal
        var entraObjectId = principal.GetEntraObjectId();
        if (entraObjectId is null)
        {
            _logger.LogWarning("Unable to sync user: missing Entra object id claim.");
            return null;
        }

        var email = NormalizeEmail(TryGetEmail(principal));
        var displayName = TrimToMaxLength(TryGetDisplayName(principal), maxLength: 200);

        var user = await _dbContext.Users.SingleOrDefaultAsync(u => u.EntraObjectId == entraObjectId.Value);

        if (user is null)
        {
            user = new User
            {
                EntraObjectId = entraObjectId.Value,
                Email = email,
                DisplayName = displayName
            };

            _dbContext.Users.Add(user);

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // If two requests try to create the user at the same time, the unique index on EntraObjectId can throw.
                // In that case, re-fetch and proceed to update fields if needed.
                _logger.LogWarning(ex, "DbUpdateException while creating user for EntraObjectId {EntraObjectId}.", entraObjectId);
                _dbContext.ChangeTracker.Clear();
                user = await _dbContext.Users.SingleOrDefaultAsync(u => u.EntraObjectId == entraObjectId.Value);

                if (user is null)
                {
                    throw;
                }
            }
        }

        var changed = false;

        if (!string.IsNullOrWhiteSpace(email) && !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            user.Email = email;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(user.DisplayName, displayName, StringComparison.Ordinal))
        {
            user.DisplayName = displayName;
            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync();
        }

        return user.UserId;
    }

    public async Task<List<string>> GetRolesForUser(ClaimsPrincipal principal)
    {
        var entraObjectId = principal.GetEntraObjectId();
        if (entraObjectId is null)
        {
            _logger.LogWarning("Unable to load roles: missing Entra object id claim.");
            return [];
        }

        // Join explicitly to work consistently across providers (including EFCore InMemory in tests).
        var roleNames = await _dbContext.UserRoles
            .Join(_dbContext.Users, ur => ur.UserId, u => u.UserId, (ur, u) => new { ur, u })
            .Where(x => x.u.EntraObjectId == entraObjectId.Value)
            .Join(_dbContext.Roles, x => x.ur.RoleId, r => r.RoleId, (x, r) => r.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();

        return roleNames;
    }

    public async Task<ClaimsPrincipal?> UpdateUserPrincipalIfNeeded(ClaimsPrincipal principal)
    {
        var dbUserId = await SyncUserAsync(principal);
        var userIdMissing = principal.GetUserId() is null && dbUserId is not null;

        // might want to cache w/ IMemoryCache to avoid DB hits on every request, but we'll skip that for simplicity
        var currentRoles = await GetRolesForUser(principal);

        // compare roles to existing claims, only update if different
        var cookieRoles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var currentRoleSet = new HashSet<string>(currentRoles, StringComparer.OrdinalIgnoreCase);
        var cookieRoleSet = new HashSet<string>(cookieRoles, StringComparer.OrdinalIgnoreCase);
        var rolesChanged = !currentRoleSet.SetEquals(cookieRoleSet);
        var changed = rolesChanged || userIdMissing;

        if (!changed) { return null; } // no change

        // create new identity with updated roles
        var newId = new ClaimsIdentity(principal.Claims, authenticationType: principal.Identity?.AuthenticationType);

        if (userIdMissing)
        {
            foreach (var claim in newId.FindAll(ClaimsPrincipalExtensions.AppUserIdClaimType).ToList())
            {
                newId.RemoveClaim(claim);
            }

            newId.AddClaim(new Claim(
                ClaimsPrincipalExtensions.AppUserIdClaimType,
                dbUserId!.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (rolesChanged)
        {
            foreach (var roleClaim in newId.FindAll(ClaimTypes.Role).ToList())
            {
                newId.RemoveClaim(roleClaim);
            }

            foreach (var role in currentRoles)
            {
                newId.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        // create new principal and return it
        return new ClaimsPrincipal(newId);
    }

    private static string? TryGetEmail(ClaimsPrincipal principal)
    {
        var value =
            principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("upn")?.Value
            ?? principal.FindFirst("email")?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? TryGetDisplayName(ClaimsPrincipal principal)
    {
        var value = principal.FindFirst("name")?.Value
                    ?? principal.FindFirst(ClaimTypes.Name)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        // Keep original casing for display, but trim to protect column constraints.
        return TrimToMaxLength(email.Trim(), maxLength: 320);
    }

    private static string? TrimToMaxLength(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
