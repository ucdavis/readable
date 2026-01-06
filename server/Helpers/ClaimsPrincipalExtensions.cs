using System.Security.Claims;

namespace server.Helpers;

public static class ClaimsPrincipalExtensions
{
    public const string AppUserIdClaimType = "app_user_id";

    public static long? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(AppUserIdClaimType)?.Value;
        return long.TryParse(value, out var userId) ? userId : null;
    }

    public static Guid? GetEntraObjectId(this ClaimsPrincipal principal)
    {
        var claimTypes = new[]
        {
            "http://schemas.microsoft.com/identity/claims/objectidentifier",
            "oid",
            ClaimTypes.NameIdentifier,
        };

        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (Guid.TryParse(value, out var guid))
            {
                return guid;
            }
        }

        return null;
    }
}
