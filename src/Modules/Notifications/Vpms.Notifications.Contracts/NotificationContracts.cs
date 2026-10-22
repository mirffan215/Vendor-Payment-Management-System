namespace Vpms.Notifications.Contracts;

public static class NotificationKinds
{
    public const string ApprovalAssigned = "ApprovalAssigned";
    public const string ApprovalReminder = "ApprovalReminder";
    public const string ApprovalEscalated = "ApprovalEscalated";
    public const string ExceptionAssigned = "ExceptionAssigned";
    public const string BankChangePending = "BankChangePending";
    public const string PaymentAwaitingApproval = "PaymentAwaitingApproval";
    public const string PaymentReleased = "PaymentReleased";
    public const string ErpDeadLetter = "ErpDeadLetter";
    public const string ReconciliationDifferences = "ReconciliationDifferences";

    public static readonly IReadOnlyList<string> All =
    [
        ApprovalAssigned, ApprovalReminder, ApprovalEscalated, ExceptionAssigned, BankChangePending, PaymentAwaitingApproval, PaymentReleased, ErpDeadLetter, ReconciliationDifferences,
    ];

    /// <summary>People must not be able to switch these off: missing one means an approval, or a check on bank details, silently does not happen.</summary>
    public static readonly IReadOnlySet<string> Mandatory = new HashSet<string> { ApprovalAssigned, ApprovalReminder, ApprovalEscalated, BankChangePending };
}

/// <summary>
/// Something a person should hear about. Modules describe events; the notification module decides who is told, how, and in which language.
/// The dedupe key identifies the event, so a sweep that sees it twice tells nobody twice.
/// </summary>
/// <param name="Users">Explicit recipients; when empty, everyone holding <paramref name="Permission"/> for <paramref name="LegalEntityId"/>.</param>
/// <param name="Path">The place in the app that deals with it, for example "/approvals/{taskId}".</param>
/// <param name="Data">Values for the message template (for example "detail", "count").</param>
public sealed record NotificationEvent(
    string Kind, string DedupeKey, Guid? LegalEntityId, string SubjectLabel, IReadOnlyList<Guid> Users, string? Permission, string Path, IReadOnlyDictionary<string, string> Data, DateTimeOffset OccurredAt);

/// <summary>Implemented by each module that has things people should be told about. Read-only.</summary>
public interface INotificationSource
{
    string Name { get; }

    /// <summary>Events that occurred at or after <paramref name="since"/> (the caller overlaps windows; duplicates are harmless).</summary>
    Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default);
}
