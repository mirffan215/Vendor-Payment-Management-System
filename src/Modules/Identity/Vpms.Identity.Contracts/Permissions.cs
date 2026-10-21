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

    public const string EntityView = "Entity.View";
    public const string EntityManage = "Entity.Manage";
    public const string BrandingManage = "Branding.Manage";
    public const string ProviderManage = "Provider.Manage";
    public const string AuthorityManage = "Authority.Manage";

    public const string VendorView = "Vendor.View";
    public const string VendorEdit = "Vendor.Edit";
    public const string VendorApprove = "Vendor.Approve";
    public const string VendorPortalManage = "Vendor.Portal.Manage";
    public const string VendorBankDetailsView = "Vendor.BankDetails.View";
    public const string VendorBankDetailsReveal = "Vendor.BankDetails.Reveal";
    public const string VendorBankDetailsEdit = "Vendor.BankDetails.Edit";
    public const string VendorBankDetailsApprove = "Vendor.BankDetails.Approve";

    public const string InvoiceView = "Invoice.View";
    public const string InvoiceCreate = "Invoice.Create";
    public const string InvoiceEdit = "Invoice.Edit";
    public const string InvoiceApprove = "Invoice.Approve";
    public const string InvoiceExceptionResolve = "Invoice.Exception.Resolve";
    public const string InvoiceExceptionOverride = "Invoice.Exception.Override";
    public const string InvoicePoImport = "Invoice.PO.Import";
    public const string InvoiceIntakeManage = "Invoice.Intake.Manage";
    public const string VerificationConfigure = "Verification.Configure";
    public const string VerificationApproveChange = "Verification.ApproveChange";

    public const string PaymentPrepare = "Payment.Prepare";
    public const string PaymentRelease = "Payment.Release";

    public static readonly IReadOnlyList<string> All =
    [
        TenantAdmin, UserManage, RoleManage, AuditRead,
        EntityView, EntityManage, BrandingManage, ProviderManage, AuthorityManage,
        VendorView, VendorEdit, VendorApprove, VendorPortalManage,
        VendorBankDetailsView, VendorBankDetailsReveal, VendorBankDetailsEdit, VendorBankDetailsApprove,
        InvoiceView, InvoiceCreate, InvoiceEdit, InvoiceApprove, InvoiceExceptionResolve, InvoiceExceptionOverride, InvoicePoImport, InvoiceIntakeManage,
        VerificationConfigure, VerificationApproveChange,
        PaymentPrepare, PaymentRelease,
    ];
}
