using System.Globalization;

namespace Vpms.Reporting.Domain;

public static class ReportKeys
{
    public const string PayablesAging = "payables-aging";
    public const string PaymentForecast = "payment-forecast";
    public const string Exceptions = "exceptions";
    public const string ApprovalDelays = "approval-delays";
    public const string VatRegister = "input-vat-register";

    public static readonly IReadOnlyList<string> All = [PayablesAging, PaymentForecast, Exceptions, ApprovalDelays, VatRegister];
}

public static class AgingBucket
{
    public const string NotDue = "NotDue";
    public const string D1To30 = "D1To30";
    public const string D31To60 = "D31To60";
    public const string D61To90 = "D61To90";
    public const string Over90 = "Over90";

    public static readonly IReadOnlyList<string> Order = [NotDue, D1To30, D31To60, D61To90, Over90];

    /// <param name="daysPastDue">Days after the due date; zero or negative means not yet due.</param>
    public static string Of(int daysPastDue) => daysPastDue switch
    {
        <= 0 => NotDue,
        <= 30 => D1To30,
        <= 60 => D31To60,
        <= 90 => D61To90,
        _ => Over90,
    };
}

public static class PaymentStage
{
    public const string NoRequest = "NoRequest";
    public const string Created = "Created";
    public const string InProposal = "InProposal";

    public static string Of(string? requestStatus) => requestStatus switch
    {
        "Created" => Created,
        "InProposal" => InProposal,
        _ => NoRequest,
    };
}

public static class ReportDates
{
    /// <summary>The Monday of the week the date falls in (weeks start on Monday).</summary>
    public static DateOnly WeekStart(DateOnly date)
    {
        var back = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-back);
    }

    /// <summary>The Umm al-Qura (Hijri) date as yyyy-MM-dd, or null outside the calendar's supported range.</summary>
    public static string? Hijri(DateOnly date)
    {
        var calendar = new UmAlQuraCalendar();
        var dt = date.ToDateTime(TimeOnly.MinValue);
        if (dt < calendar.MinSupportedDateTime || dt > calendar.MaxSupportedDateTime) return null;
        return string.Create(CultureInfo.InvariantCulture, $"{calendar.GetYear(dt):0000}-{calendar.GetMonth(dt):00}-{calendar.GetDayOfMonth(dt):00}");
    }
}
