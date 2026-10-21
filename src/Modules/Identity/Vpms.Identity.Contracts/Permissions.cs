namespace Vpms.Identity.Contracts;

/// <summary>
/// Catalogue of permissions. Names are stable identifiers stored in the database. Permissions for modules built
/// in later phases are reserved here so segregation-of-duties rules can already reference them.
/// </summary>
public static class Permissions
{
    public const string TenantAdmin = "Tenant.Admin";
    public const string UserManage = "User.Manage";
    public const string RoleManage = "Role.Manage";
    public const string AuditRead = "Audit.Read";

    public const string VendorEdit = "Vendor.Edit";
    public const string VendorBankDetailsEdit = "Vendor.BankDetails.Edit";
    public const string VendorBankDetailsApprove = "Vendor.BankDetails.Approve";

    public const string InvoiceCreate = "Invoice.Create";
    public const string InvoiceApprove = "Invoice.Approve";

    public const string PaymentPrepare = "Payment.Prepare";
    public const string PaymentRelease = "Payment.Release";

    public static readonly IReadOnlyList<string> All =
    [
        TenantAdmin, UserManage, RoleManage, AuditRead,
        VendorEdit, VendorBankDetailsEdit, VendorBankDetailsApprove,
        InvoiceCreate, InvoiceApprove,
        PaymentPrepare, PaymentRelease,
    ];
}
