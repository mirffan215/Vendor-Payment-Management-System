using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;

namespace Vpms.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/session")]
[Authorize(Policy = VpmsAuth.InternalPolicy)]
public sealed class SessionController(IAntiforgery antiforgery) : ControllerBase
{
    /// <summary>The signed-in user, their tenant, and the antiforgery token to send with state-changing calls.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new
        {
            userId = User.FindFirstValue(VpmsClaims.UserId),
            tenantId = User.FindFirstValue(VpmsClaims.TenantId),
            email = User.FindFirstValue("email"),
            displayName = User.FindFirstValue("name"),
            locale = User.FindFirstValue("locale"),
            csrfToken = tokens.RequestToken,
        });
    }
}
