namespace Vpms.BuildingBlocks.Jobs;

/// <summary>The kinds of data a retention period is set for (ADR-0024).</summary>
public static class RetentionClasses
{
    /// <summary>Invoices, approvals, payment instructions and ERP history: the records a tax or audit authority may ask for. Never disposed automatically in v1.</summary>
    public const string FinancialRecords = "FinancialRecords";

    /// <summary>Supplier master data, bank details and documents. Never disposed automatically in v1.</summary>
    public const string VendorData = "VendorData";

    /// <summary>The audit trail. Never edited or deleted in v1.</summary>
    public const string Audit = "Audit";

    /// <summary>In-app notifications and their delivery rows. Disposed automatically.</summary>
    public const string Notifications = "Notifications";

    /// <summary>Reminders and escalation notices from the approval workflow. Disposed automatically.</summary>
    public const string WorkflowNotices = "WorkflowNotices";

    public static readonly IReadOnlyList<string> All = [FinancialRecords, VendorData, Audit, Notifications, WorkflowNotices];

    /// <summary>The classes the retention job deletes. The others are only reported.</summary>
    public static readonly IReadOnlyList<string> Disposed = [Notifications, WorkflowNotices];
}

/// <summary>What applies until a tenant sets its own policy. Months, so "six years" and "six months" use one unit.</summary>
public static class RetentionDefaults
{
    public static readonly IReadOnlyDictionary<string, int> Months = new Dictionary<string, int>
    {
        [RetentionClasses.FinancialRecords] = 84,
        [RetentionClasses.VendorData] = 84,
        [RetentionClasses.Audit] = 120,
        [RetentionClasses.Notifications] = 6,
        [RetentionClasses.WorkflowNotices] = 6,
    };
}

/// <summary>
/// What a retention pass needs to know for one tenant: how long each kind of data is kept (per country of the legal entity where that applies)
/// and what is under legal hold. Built from the tenant's active policy by the customer-administration module.
/// </summary>
public sealed class RetentionContext(DateTimeOffset now, Func<string, string?, int> months, bool tenantHeld, IReadOnlySet<Guid> heldEntities, IReadOnlySet<Guid> heldVendors)
{
    public DateTimeOffset Now { get; } = now;

    /// <summary>A legal hold on the whole tenant stops all disposal.</summary>
    public bool TenantHeld { get; } = tenantHeld;

    public IReadOnlySet<Guid> HeldEntities { get; } = heldEntities;

    public IReadOnlySet<Guid> HeldVendors { get; } = heldVendors;

    public int MonthsFor(string dataClass, string? countryCode = null) => months(dataClass, countryCode);

    /// <summary>Records older than this have passed their retention period.</summary>
    public DateTimeOffset CutoffFor(string dataClass, string? countryCode = null) => Now.AddMonths(-MonthsFor(dataClass, countryCode));
}

/// <summary>
/// One kind of data a module can say is past its retention period and, where the decision has been taken to do so, delete. Implementations never
/// delete anything covered by a hold in the context.
/// </summary>
public interface IRetentionTarget
{
    string DataClass { get; }

    /// <summary>False for data that is only reported, never deleted by the system (financial records, supplier data, audit).</summary>
    bool CanDispose { get; }

    Task<int> CountEligibleAsync(RetentionContext context, CancellationToken ct = default);

    /// <summary>Deletes up to <paramref name="batch"/> eligible rows and returns how many. Only called when <see cref="CanDispose"/> is true.</summary>
    Task<int> DisposeAsync(RetentionContext context, int batch, CancellationToken ct = default);
}
