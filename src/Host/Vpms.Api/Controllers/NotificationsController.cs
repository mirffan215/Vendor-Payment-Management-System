using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Application;

namespace Vpms.Api.Controllers;

public sealed record PreferenceRequest(string Kind, string Channel, bool Enabled);

public sealed record TemplateRequest(string Kind, string Channel, string Locale, string Subject, string Body);

/// <summary>A person's own notifications and preferences, and (with Notification.Configure) the wording of the messages.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class NotificationsController(INotificationService notifications, INotificationAdmin admin, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("notifications")]
    public async Task<IActionResult> List([FromQuery] bool unread = false, [FromQuery] int skip = 0, [FromQuery] int take = 30, [FromQuery] string locale = "en", CancellationToken ct = default) =>
        Ok(await notifications.ListAsync(unread, skip, take, locale, ct));

    [HttpGet("notifications/unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) => Ok(new { count = await notifications.UnreadCountAsync(ct) });

    [HttpPost("notifications/{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await notifications.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct) => Ok(new { marked = await notifications.MarkAllReadAsync(ct) });

    [HttpGet("notifications/preferences")]
    public async Task<IActionResult> Preferences(CancellationToken ct) => Ok(await notifications.PreferencesAsync(ct));

    [HttpPut("notifications/preferences")]
    public async Task<IActionResult> SetPreference(PreferenceRequest body, CancellationToken ct) => Ok(await notifications.SetPreferenceAsync(body.Kind, body.Channel, body.Enabled, ct));

    [HttpGet("admin/notification-templates")]
    public async Task<IActionResult> Templates(CancellationToken ct) => await CanConfigure(ct) ? Ok(await admin.TemplatesAsync(ct)) : Forbid();

    [HttpPut("admin/notification-templates")]
    public async Task<IActionResult> Upsert(TemplateRequest body, CancellationToken ct) =>
        await CanConfigure(ct) ? Ok(await admin.UpsertTemplateAsync(body.Kind, body.Channel, body.Locale, body.Subject, body.Body, ct)) : Forbid();

    [HttpDelete("admin/notification-templates")]
    public async Task<IActionResult> Reset([FromQuery] string kind, [FromQuery] string channel, [FromQuery] string locale, CancellationToken ct)
    {
        if (!await CanConfigure(ct)) return Forbid();
        await admin.ResetTemplateAsync(kind, channel, locale, ct);
        return NoContent();
    }

    [HttpPost("admin/notification-templates/preview")]
    public async Task<IActionResult> Preview(TemplateRequest body, CancellationToken ct) => await CanConfigure(ct) ? Ok(admin.Preview(body.Kind, body.Locale, body.Subject, body.Body)) : Forbid();

    private async Task<bool> CanConfigure(CancellationToken ct) =>
        await permissions.HasAsync(Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!), Permissions.NotificationConfigure, null, ct);
}
