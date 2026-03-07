using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace JoshHeaps.Net.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BlogController(IBlogService blogService, IConfiguration configuration) : ControllerBase
{
    [HttpPost("invalidate")]
    public IActionResult InvalidateCache([FromHeader(Name = "X-Invalidate-Key")] string? key)
    {
        var expectedKey = configuration["BlogApi:InvalidateKey"];
        if (string.IsNullOrEmpty(expectedKey) || key != expectedKey)
            return Unauthorized();

        blogService.ClearCache();
        return Ok();
    }
}
