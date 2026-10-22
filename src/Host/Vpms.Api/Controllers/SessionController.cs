using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

public sealed record PreferencesRequest(string Locale, string Calendar);

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/session")]
[Authorize(Policy = VpmsAuth.InternalPolicy)]
public sealed class SessionController(IAntiforgery antiforgery, IUserPreferences preferences) : ControllerBase
{
    /// <summary>The signed-in user, their tenant, and the antiforgery token to send with state-changing calls.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var mine = await preferences.GetAsync(Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!), ct);
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new
        {
            userId = User.FindFirstValue(VpmsClaims.UserId),
            tenantId = User.FindFirstValue(VpmsClaims.TenantId),
            email = User.FindFirstValue("email"),
            displayName = User.FindFirstValue("name"),
            locale = mine.Locale,
            calendar = mine.Calendar,
            csrfToken = tokens.RequestToken,
        });
    }

    /// <summary>Saves the signed-in person's language and calendar.</summary>
    [HttpPut("preferences")]
    public async Task<IActionResult> SetPreferences(PreferencesRequest body, CancellationToken ct) => Ok(await preferences.SetMineAsync(body.Locale, body.Calendar, ct));
}
