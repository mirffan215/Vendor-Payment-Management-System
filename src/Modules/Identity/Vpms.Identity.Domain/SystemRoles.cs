using Vpms.Identity.Contracts;

namespace Vpms.Identity.Domain;

/// <summary>Default roles created for every tenant. Tenants can add their own roles on top.</summary>
public static class SystemRoles
{
    public static readonly IReadOnlyDictionary<string, string[]> Definitions = new Dictionary<string, string[]>
    {
        ["Tenant Administrator"] = [Permissions.TenantAdmin, Permissions.UserManage, Permissions.RoleManage, Permissions.AuditRead],
        ["Auditor"] = [Permissions.AuditRead],
        ["Vendor Manager"] = [Permissions.VendorEdit, Permissions.VendorBankDetailsEdit],
        ["Bank Details Approver"] = [Permissions.VendorBankDetailsApprove],
        ["Invoice Clerk"] = [Permissions.InvoiceCreate],
        ["Invoice Approver"] = [Permissions.InvoiceApprove],
        ["Payment Preparer"] = [Permissions.PaymentPrepare],
        ["Payment Releaser"] = [Permissions.PaymentRelease],
    };
}
