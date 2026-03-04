using Microsoft.AspNetCore.Mvc;
using server.Helpers;
using Server.Services;

namespace Server.Controllers;

public class ApiKeyController(IApiKeyService apiKeyService) : ApiControllerBase
{
    /// <summary>
    /// Returns information about the caller's current API key (hint and creation date),
    /// or indicates that no key exists.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiKeyInfoResponse>> GetApiKeyInfo(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var info = await apiKeyService.GetApiKeyInfoAsync(userId.Value, ct);
        if (info is null)
        {
            return Ok(new ApiKeyInfoResponse(Exists: false, KeyHint: null, CreatedAt: null));
        }

        return Ok(new ApiKeyInfoResponse(Exists: true, KeyHint: info.Value.KeyHint, CreatedAt: info.Value.CreatedAt));
    }

    /// <summary>
    /// Generates (or regenerates) an API key for the caller.
    /// The raw key is returned ONLY in this response — it is never stored and cannot be retrieved again.
    /// </summary>
    [HttpPost]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<GenerateApiKeyResponse>> GenerateApiKey(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        var (rawKey, keyHint, createdAt) = await apiKeyService.GenerateApiKeyAsync(userId.Value, ct);

        return Ok(new GenerateApiKeyResponse(RawKey: rawKey, KeyHint: keyHint, CreatedAt: createdAt));
    }

    /// <summary>
    /// Revokes the caller's current API key.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> RevokeApiKey(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null) return Unauthorized();

        await apiKeyService.RevokeApiKeyAsync(userId.Value, ct);

        return NoContent();
    }
}

public record ApiKeyInfoResponse(bool Exists, string? KeyHint, DateTimeOffset? CreatedAt);
public record GenerateApiKeyResponse(string RawKey, string KeyHint, DateTimeOffset CreatedAt);
