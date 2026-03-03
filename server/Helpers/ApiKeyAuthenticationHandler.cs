using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Server.Services;
using server.Helpers;

namespace server.Helpers;

/// <summary>
/// Authentication scheme that validates API keys supplied in the
/// <c>Authorization: ApiKey &lt;key&gt;</c> header.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeyService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderPrefix = "ApiKey ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return AuthenticateResult.NoResult();
        }

        var header = authHeader.ToString();
        if (!header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = header[HeaderPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(rawKey))
        {
            return AuthenticateResult.Fail("Empty API key.");
        }

        var userId = await apiKeyService.ValidateApiKeyAsync(rawKey, Context.RequestAborted);
        if (userId is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new[] { new Claim(ClaimsPrincipalExtensions.AppUserIdClaimType, userId.Value.ToString()) };
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
