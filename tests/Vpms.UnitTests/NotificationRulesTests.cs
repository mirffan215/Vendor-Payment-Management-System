using Vpms.Notifications.Contracts;
using Vpms.Notifications.Domain;

namespace Vpms.UnitTests;

public sealed class NotificationRulesTests
{
    private static readonly Dictionary<string, string> Values = new() { ["subject"] = "INV-1 <b>x</b>", ["detail"] = "level 1", ["url"] = "https://app/x?a=1&b=2" };

    [Fact]
    public void Placeholders_are_filled_and_unknown_ones_are_left_blank()
    {
        Assert.Equal("Approval: INV-1 <b>x</b> (level 1)", TemplateEngine.Render("Approval: {{subject}} ({{ detail }})", Values, false));
        Assert.Equal("a--b", TemplateEngine.Render("a-{{count}}-b", Values, false));
    }

    [Fact]
    public void Html_values_are_escaped_so_a_vendor_name_cannot_inject_markup_into_an_email()
    {
        var html = TemplateEngine.Render("{{subject}} {{url}}", Values, true);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
        Assert.Contains("a=1&amp;b=2", html);
    }

    [Fact]
    public void A_value_is_never_treated_as_a_template_itself()
    {
        var evil = new Dictionary<string, string> { ["subject"] = "{{url}}", ["url"] = "SECRET" };
        Assert.Equal("{{url}}", TemplateEngine.Render("{{subject}}", evil, false));
    }

    [Theory]
    [InlineData("Hello {{subject}}", "Body {{url}}", 0)]
    [InlineData("", "Body", 1)]
    [InlineData("Hi", "", 1)]
    [InlineData("Hi {{password}}", "Body", 1)]
    [InlineData("Hi {{subject", "Body", 1)]
    [InlineData("Hi", "Body }}", 1)]
    public void Templates_are_checked_before_they_are_stored(string subject, string body, int problems) => Assert.Equal(problems, TemplateEngine.Validate(subject, body).Count);

    [Fact]
    public void Length_limits_apply()
    {
        Assert.Contains("subject_too_long", TemplateEngine.Validate(new string('a', TemplateEngine.MaxSubject + 1), "ok"));
        Assert.Contains("body_too_long", TemplateEngine.Validate("ok", new string('a', TemplateEngine.MaxBody + 1)));
    }

    [Fact]
    public void Every_kind_has_valid_built_in_wording_in_both_languages_and_the_arabic_is_arabic()
    {
        foreach (var kind in NotificationKinds.All)
        {
            Assert.True(BuiltInTemplates.Exists(kind), kind);
            foreach (var locale in new[] { "en", "ar" })
            {
                var (subject, body) = BuiltInTemplates.Get(kind, locale);
                Assert.Empty(TemplateEngine.Validate(subject, body));
                if (locale == "ar")
                {
                    Assert.Matches(@"\p{IsArabic}", subject);
                    Assert.Matches(@"\p{IsArabic}", body);
                }
            }
        }
    }

    [Fact]
    public void Approvals_and_bank_changes_cannot_be_switched_off_but_other_kinds_can()
    {
        Assert.Contains(NotificationKinds.ApprovalAssigned, NotificationKinds.Mandatory);
        Assert.Contains(NotificationKinds.BankChangePending, NotificationKinds.Mandatory);
        Assert.DoesNotContain(NotificationKinds.PaymentReleased, NotificationKinds.Mandatory);
        Assert.All(NotificationKinds.Mandatory, k => Assert.Contains(k, NotificationKinds.All));
    }

    [Fact]
    public void The_release_message_says_no_money_was_sent() =>
        Assert.Contains("not sent any money", BuiltInTemplates.Get(NotificationKinds.PaymentReleased, "en").Body);
}
