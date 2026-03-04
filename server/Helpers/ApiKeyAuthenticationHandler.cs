using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Services;
using server.core.Data;
using server.Helpers;

namespace server.Helpers;

/// <summary>
/// Authentication scheme that validates API keys supplied in the
/// <c>Authorization: ApiKey &lt;key&gt;</c> header or the
/// <c>X-Api-Key</c> header.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeyService,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderPrefix = "ApiKey ";
    public const string CustomHeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? rawKey = null;

        // Check the custom X-Api-Key header first (used by Swagger UI)
        if (Request.Headers.TryGetValue(CustomHeaderName, out var customHeader))
        {
            rawKey = customHeader.ToString().Trim();
        }
        // Fall back to Authorization: ApiKey {key}
        else if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var header = authHeader.ToString();
            if (!header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.NoResult();
            }
            rawKey = header[HeaderPrefix.Length..].Trim();
        }
        else
        {
            return AuthenticateResult.NoResult();
        }
        if (string.IsNullOrEmpty(rawKey))
        {
            return AuthenticateResult.Fail("Empty API key.");
        }

        var userId = await apiKeyService.ValidateApiKeyAsync(rawKey, Context.RequestAborted);
        if (userId is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        // Load user profile so the principal carries the same claims as a
        // cookie-authenticated session (name, email, roles).
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.UserId == userId.Value)
            .Select(u => new
            {
                u.UserId,
                u.DisplayName,
                u.Email,
                Roles = u.UserRoles.Select(ur => ur.Role!.Name).ToList()
            })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (user is null)
        {
            return AuthenticateResult.Fail("API key is valid but the associated user no longer exists.");
        }

        var claims = new List<Claim>
        {
            new(ClaimsPrincipalExtensions.AppUserIdClaimType, user.UserId.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            claims.Add(new Claim("name", user.DisplayName));

        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim("preferred_username", user.Email));

        foreach (var role in user.Roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
