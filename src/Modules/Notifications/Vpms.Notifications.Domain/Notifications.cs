using System.Net;
using System.Text.RegularExpressions;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Notifications.Domain;

public static class NotificationChannel
{
    public const string InApp = "InApp";
    public const string Email = "Email";
    public const string Webhook = "Webhook";
    public const string Push = "Push";

    public static readonly IReadOnlyList<string> All = [InApp, Email, Webhook, Push];
}

/// <summary>One message to one person. It is also what the in-app inbox lists; other channels deliver from it. Text is rendered when read or sent, in the reader's language.</summary>
public class Notification : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public Guid? LegalEntityId { get; set; }
    public string SubjectLabel { get; set; } = "";
    public string Path { get; set; } = "";
    public string DataJson { get; set; } = "{}";

    /// <summary>Identifies the event for this person; the same event is never recorded twice.</summary>
    public string DedupeKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>How far each source has been read, so a sweep asks only for what is new (with an overlap; the dedupe key absorbs repeats).</summary>
public class NotificationCursor : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset LastPolledAt { get; set; }
}

public class NotificationPreference : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A tenant's own wording for one kind, channel and language. Without one, the built-in wording is used.</summary>
public class NotificationTemplate : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Locale { get; set; } = "en";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Templates are plain text with {{placeholders}} from a fixed list. There is no logic and no code in them, so a tenant can reword a
/// message but cannot make it do anything.
/// </summary>
public static partial class TemplateEngine
{
    public static readonly IReadOnlyList<string> Placeholders = ["subject", "detail", "count", "url"];
    public const int MaxSubject = 150;
    public const int MaxBody = 2000;

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+)\s*\}\}")]
    private static partial Regex Pattern();

    /// <summary>Problems with a template: unknown placeholders, stray braces, or text that is too long. Empty means it is fine.</summary>
    public static IReadOnlyList<string> Validate(string subject, string body)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(subject)) problems.Add("subject_required");
        if (string.IsNullOrWhiteSpace(body)) problems.Add("body_required");
        if (subject.Length > MaxSubject) problems.Add("subject_too_long");
        if (body.Length > MaxBody) problems.Add("body_too_long");
        foreach (var text in new[] { subject, body })
        {
            foreach (Match m in Pattern().Matches(text))
            {
                if (!Placeholders.Contains(m.Groups[1].Value)) problems.Add("unknown_placeholder:" + m.Groups[1].Value);
            }

            if (Pattern().Replace(text, "").Contains("{{") || Pattern().Replace(text, "").Contains("}}")) problems.Add("stray_braces");
        }

        return problems.Distinct().ToList();
    }

    /// <summary>Fills the placeholders. Values are never interpreted as templates themselves; for HTML each value is escaped.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, bool escapeHtml) =>
        Pattern().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            var value = values.TryGetValue(key, out var v) ? v : "";
            return escapeHtml ? WebUtility.HtmlEncode(value) : value;
        });
}

/// <summary>The wording used when a tenant has not set its own.</summary>
public static class BuiltInTemplates
{
    private static readonly Dictionary<string, (string EnTitle, string EnBody, string ArTitle, string ArBody)> Words = new()
    {
        ["ApprovalAssigned"] = ("Approval needed: {{subject}}", "You have a new approval task ({{detail}}). Open it to decide: {{url}}", "مطلوب اعتمادك: {{subject}}", "لديك مهمة اعتماد جديدة ({{detail}}). افتحها لاتخاذ القرار: {{url}}"),
        ["ApprovalReminder"] = ("Reminder: {{subject}}", "This approval is still waiting for your decision: {{url}}", "تذكير: {{subject}}", "هذا الاعتماد ما زال بانتظار قرارك: {{url}}"),
        ["ApprovalEscalated"] = ("Escalated: {{subject}}", "An approval has been escalated and needs attention: {{url}}", "تم التصعيد: {{subject}}", "جرى تصعيد اعتماد ويحتاج إلى انتباهك: {{url}}"),
        ["ExceptionAssigned"] = ("Exception assigned to you: {{subject}}", "The rule {{detail}} needs your review: {{url}}", "استثناء مُسند إليك: {{subject}}", "القاعدة {{detail}} تحتاج إلى مراجعتك: {{url}}"),
        ["BankChangePending"] = ("Bank details change waiting: {{subject}}", "A change to this vendor's bank details needs a second person to verify it: {{url}}", "تغيير بيانات بنكية بانتظار القرار: {{subject}}", "تغيير في البيانات البنكية لهذا المورد يحتاج إلى شخص ثانٍ للتحقق منه: {{url}}"),
        ["PaymentAwaitingApproval"] = ("Payment proposal to approve: {{subject}}", "A payment proposal was submitted and needs treasury approval: {{url}}", "مقترح دفع بانتظار اعتمادك: {{subject}}", "قُدّم مقترح دفع ويحتاج إلى اعتماد الخزينة: {{url}}"),
        ["PaymentReleased"] = ("Payment proposal released: {{subject}}", "The payment instruction was prepared. This system has not sent any money: {{url}}", "تم إصدار مقترح الدفع: {{subject}}", "جُهّزت تعليمات الدفع. لم يرسل هذا النظام أي أموال: {{url}}"),
        ["ErpDeadLetter"] = ("ERP message needs attention: {{subject}}", "A message to the ERP was refused or kept failing ({{detail}}): {{url}}", "رسالة نظام تخطيط الموارد تحتاج إلى انتباه: {{subject}}", "رُفضت رسالة إلى نظام تخطيط الموارد أو استمر فشلها ({{detail}}): {{url}}"),
        ["ReconciliationDifferences"] = ("ERP reconciliation found differences: {{subject}}", "{{count}} new difference(s) need to be accounted for: {{url}}", "المطابقة مع نظام تخطيط الموارد وجدت فروقات: {{subject}}", "{{count}} فروقات جديدة تحتاج إلى معالجة: {{url}}"),
    };

    public static (string Subject, string Body) Get(string kind, string locale) =>
        Words.TryGetValue(kind, out var w) ? (locale == "ar" ? (w.ArTitle, w.ArBody) : (w.EnTitle, w.EnBody)) : ("{{subject}}", "{{url}}");

    public static bool Exists(string kind) => Words.ContainsKey(kind);
}
