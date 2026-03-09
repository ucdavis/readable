using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Server.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public StatusController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("banner")]
    [ResponseCache(Duration = 60)]
    public IActionResult GetBanner()
    {
        var message = _configuration["STATUS_BANNER"];

        if (string.IsNullOrWhiteSpace(message))
        {
            return NoContent();
        }

        return Ok(new { message });
    }
}
