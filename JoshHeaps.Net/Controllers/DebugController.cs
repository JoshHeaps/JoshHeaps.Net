using JoshHeaps.Net.Services.Implementations;
using Microsoft.AspNetCore.Mvc;

namespace JoshHeaps.Net.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DebugController : ControllerBase
{
    [HttpGet("IpCheck")]
    public ActionResult<string> GetIpCheckingStatus()
    {
        return Ok(AutoIpUpdateService.IsEnabled.ToString());
    }
}
