using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.ErpIntegration.Domain;

/// <summary>Who the audit trail names for what the integration does on its own (queue work, pull from the ERP).</summary>
public static class ErpSystemActor
{
    public static readonly Guid Id = new("00000000-0000-0000-0000-0000e2b00001");
}

public static class SyncType
{
    public const string PostInvoice = "PostInvoice";
    public const string UpsertVendor = "UpsertVendor";

    public static readonly IReadOnlyList<string> All = [PostInvoice, UpsertVendor];
}

public static class SyncStatus
{
    /// <summary>Waiting for its turn (new, or failed and waiting for the next attempt).</summary>
    public const string Pending = "Pending";

    /// <summary>Leased by a worker. If the worker dies, the lease expires and another picks it up.</summary>
    public const string InFlight = "InFlight";

    public const string Succeeded = "Succeeded";

    /// <summary>Gave up: the ERP refused it for good, or it ran out of attempts. A person decides what happens next.</summary>
    public const string DeadLetter = "DeadLetter";

    /// <summary>A person dealt with it outside the queue (for example posted it in the ERP by hand) and said so.</summary>
    public const string Resolved = "Resolved";

    /// <summary>The object changed after the message was queued, so a newer message replaces it. Nothing was lost.</summary>
    public const string Superseded = "Superseded";
}

/// <summary>When to try again. Exponential with a cap; jitter is supplied by the caller so the policy stays deterministic.</summary>
public static class RetryPolicy
{
    public const int MaxAttempts = 8;
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Cap = TimeSpan.FromHours(1);

    /// <param name="attempt">The number of the attempt that just failed (1 for the first).</param>
    /// <param name="jitter">A value in [0, 1) that spreads retries out; 0 means no spread.</param>
    public static TimeSpan Delay(int attempt, double jitter = 0)
    {
        var seconds = Base.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(seconds, Cap.TotalSeconds);
        return TimeSpan.FromSeconds(capped * (1 + 0.25 * Math.Clamp(jitter, 0, 0.999)));
    }
}

/// <summary>
/// One message in the outbound queue (a transactional outbox row). The idempotency key makes the same business event impossible to queue
/// twice; the state methods below are the only way its status changes.
/// </summary>
public class ErpSyncRecord : ITenantOwned
{
    public const int LeaseMinutes = 5;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string Type { get; set; } = SyncType.PostInvoice;
    public Guid ObjectId { get; set; }

    /// <summary>The version of the object this message is about (invoice approved version, vendor data hash).</summary>
    public string ObjectVersion { get; set; } = "";

    /// <summary>Fingerprint of the data at queue time; a message whose object changed since is not sent.</summary>
    public string PayloadHash { get; set; } = "";

    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = SyncStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LeaseOwner { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastError { get; set; }
    public string? ErpReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? HandledBy { get; set; }
    public string? HandlingNote { get; set; }

    /// <summary>When the source object reached the state that queued it (for an invoice: its approval time). Lets the sweep resume where it stopped.</summary>
    public DateTimeOffset? SourceAt { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public static string KeyFor(Guid tenantId, Guid legalEntityId, string type, Guid objectId, string version) => $"{tenantId:N}:{legalEntityId:N}:{type}:{objectId:N}:{version}";

    public bool IsDue(DateTimeOffset now) =>
        (Status == SyncStatus.Pending && NextAttemptAt <= now) || (Status == SyncStatus.InFlight && LeaseUntil is { } l && l <= now);

    /// <summary>Takes the message for one attempt. A lease that expired counts as an attempt that failed silently.</summary>
    public void Lease(string owner, DateTimeOffset now)
    {
        if (!IsDue(now)) throw new ConflictException("not_due", "This message is not ready to be sent.");
        Status = SyncStatus.InFlight;
        LeaseOwner = owner;
        LeaseUntil = now.AddMinutes(LeaseMinutes);
        Attempts++;
    }

    /// <summary>Queued already finished: it needs a person from the start (nothing to send).</summary>
    public void DeadLetterOnArrival(string code, string message, DateTimeOffset now)
    {
        Status = SyncStatus.DeadLetter;
        LastErrorCode = code;
        LastError = Trim(message);
        CompletedAt = now;
    }

    public void Succeed(string reference, DateTimeOffset now)
    {
        RequireInFlight();
        Status = SyncStatus.Succeeded;
        ErpReference = reference;
        CompletedAt = now;
        LastErrorCode = null;
        LastError = null;
        Release();
    }

    /// <summary>A temporary failure: wait and try again, or give up when attempts run out.</summary>
    public void FailTransient(string code, string? message, DateTimeOffset now, TimeSpan? retryAfter, double jitter)
    {
        RequireInFlight();
        LastErrorCode = code;
        LastError = Trim(message);
        Release();
        if (Attempts >= RetryPolicy.MaxAttempts)
        {
            Status = SyncStatus.DeadLetter;
            CompletedAt = now;
            LastErrorCode = "max_attempts";
            LastError = Trim($"Gave up after {Attempts} attempts. Last error: {code}. {message}");
            return;
        }

        var delay = RetryPolicy.Delay(Attempts, jitter);
        if (retryAfter is { } r && r > delay) delay = r > RetryPolicy.Cap ? RetryPolicy.Cap : r;
        Status = SyncStatus.Pending;
        NextAttemptAt = now + delay;
    }

    /// <summary>Cannot proceed yet for a reason that is not the ERP's fault (for example the vendor is not in the ERP yet). Does not use up an attempt.</summary>
    public void Defer(string code, string? message, DateTimeOffset now, TimeSpan delay)
    {
        RequireInFlight();
        Attempts--;
        LastErrorCode = code;
        LastError = Trim(message);
        Status = SyncStatus.Pending;
        NextAttemptAt = now + delay;
        Release();
    }

    /// <summary>The ERP refused for good, or the message can no longer be sent. Needs a person.</summary>
    public void DeadLetterNow(string code, string? message, DateTimeOffset now)
    {
        if (Status is not (SyncStatus.InFlight or SyncStatus.Pending)) throw new ConflictException("invalid_transition", "This message is already finished.");
        Status = SyncStatus.DeadLetter;
        LastErrorCode = code;
        LastError = Trim(message);
        CompletedAt = now;
        Release();
    }

    /// <summary>The source object changed since this was queued; the message is retired without being sent.</summary>
    public void Supersede(string note, DateTimeOffset now)
    {
        if (Status is not (SyncStatus.InFlight or SyncStatus.Pending)) throw new ConflictException("invalid_transition", "This message is already finished.");
        Status = SyncStatus.Superseded;
        HandlingNote = Trim(note);
        CompletedAt = now;
        Release();
    }

    /// <summary>An operator puts a dead-lettered message back in the queue (after fixing the cause). The attempts start again.</summary>
    public void Requeue(Guid actor, string reason, DateTimeOffset now)
    {
        if (Status != SyncStatus.DeadLetter) throw new ConflictException("invalid_transition", "Only a dead-lettered message can be retried.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = SyncStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        CompletedAt = null;
        HandledBy = actor;
        HandlingNote = Trim(reason);
    }

    /// <summary>An operator records that the message was dealt with outside the queue.</summary>
    public void ResolveManually(Guid actor, string reason, string? erpReference, DateTimeOffset now)
    {
        if (Status != SyncStatus.DeadLetter) throw new ConflictException("invalid_transition", "Only a dead-lettered message can be resolved by hand.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = SyncStatus.Resolved;
        ErpReference = string.IsNullOrWhiteSpace(erpReference) ? ErpReference : erpReference.Trim();
        HandledBy = actor;
        HandlingNote = Trim(reason);
        CompletedAt = now;
    }

    private void RequireInFlight()
    {
        if (Status != SyncStatus.InFlight) throw new ConflictException("invalid_transition", "This message is not being sent.");
    }

    private void Release()
    {
        LeaseOwner = null;
        LeaseUntil = null;
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 500)];
}

/// <summary>One try at sending a message. Append-only: the history of what the ERP said.</summary>
public class ErpSyncAttempt : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RecordId { get; set; }
    public int AttemptNo { get; set; }
    public DateTimeOffset At { get; set; }
    public string Outcome { get; set; } = "";
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public static class InboundKind
{
    public const string PurchaseOrder = "PurchaseOrder";
    public const string InvoiceStatus = "InvoiceStatus";
    public const string Payment = "Payment";

    public static readonly IReadOnlyList<string> All = [PurchaseOrder, InvoiceStatus, Payment];
}

public static class InboxStatus
{
    public const string Received = "Received";
    public const string Applied = "Applied";
    public const string Failed = "Failed";
}

/// <summary>
/// Something the ERP told us. The message id is built from what it says, so the same message arriving twice (a replayed page, an overlapping
/// pull window) is stored and applied once. The latest message per object is the ERP's current word on it.
/// </summary>
public class ErpInboxMessage : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string Kind { get; set; } = InboundKind.PurchaseOrder;
    public string ExternalId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string MessageId { get; set; } = "";

    /// <summary>1 for the first message about this object, then one more for each change.</summary>
    public int Sequence { get; set; }

    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = InboxStatus.Received;
    public string? Error { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }

    public static string IdFor(Guid tenantId, Guid legalEntityId, string kind, string externalId, string contentHash) => $"{tenantId:N}:{legalEntityId:N}:{kind}:{externalId}:{contentHash}";

    public void MarkApplied(DateTimeOffset now)
    {
        Status = InboxStatus.Applied;
        AppliedAt = now;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        Status = InboxStatus.Failed;
        Error = error.Length > 500 ? error[..500] : error;
    }
}

/// <summary>Where the last pull of one kind for one entity got to, so the next pull asks only for what changed (with an overlap).</summary>
public class ErpPullState : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string Kind { get; set; } = InboundKind.PurchaseOrder;
    public DateTimeOffset LastPulledAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];
}
