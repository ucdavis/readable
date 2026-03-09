using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Server.Controllers;

public class StatusController : ApiControllerBase
{
    private readonly IConfiguration _configuration;

    public StatusController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpGet("banner")]
    [ResponseCache(Duration = 60)]
    public async Task<IActionResult> GetBanner()
    {
        var message = _configuration["STATUS_BANNER"];

        if (string.IsNullOrWhiteSpace(message))
        {
            return NoContent();
        }

        return await Task.FromResult(Ok(new { message }));
    }
}
