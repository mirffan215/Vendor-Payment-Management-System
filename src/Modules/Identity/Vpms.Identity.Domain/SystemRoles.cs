using Vpms.Identity.Contracts;

namespace Vpms.Identity.Domain;

/// <summary>Default roles created for every tenant. Tenants can add their own roles on top.</summary>
public static class SystemRoles
{
    public static readonly IReadOnlyDictionary<string, string[]> Definitions = new Dictionary<string, string[]>
    {
        ["Tenant Administrator"] =
        [
            Permissions.TenantAdmin, Permissions.UserManage, Permissions.RoleManage, Permissions.AuditRead,
            Permissions.EntityView, Permissions.EntityManage, Permissions.BrandingManage, Permissions.ProviderManage,
            Permissions.AuthorityManage, Permissions.VendorView,
        ],
        ["Auditor"] = [Permissions.AuditRead, Permissions.EntityView, Permissions.VendorView],
        ["Vendor Manager"] =
        [
            Permissions.EntityView, Permissions.VendorView, Permissions.VendorEdit, Permissions.VendorPortalManage,
            Permissions.VendorBankDetailsView, Permissions.VendorBankDetailsEdit,
        ],
        ["Vendor Approver"] = [Permissions.EntityView, Permissions.VendorView, Permissions.VendorApprove],
        ["Bank Details Approver"] = [Permissions.VendorView, Permissions.VendorBankDetailsView, Permissions.VendorBankDetailsApprove, Permissions.VendorBankDetailsReveal],
        ["Invoice Clerk"] =
        [
            Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.InvoiceCreate, Permissions.InvoiceEdit,
            Permissions.InvoiceExceptionResolve,
        ],
        ["Invoice Intake Manager"] = [Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.InvoiceIntakeManage],
        ["Invoice Exception Reviewer"] = [Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.InvoiceExceptionOverride],
        ["Verification Administrator"] = [Permissions.EntityView, Permissions.VerificationConfigure, Permissions.InvoicePoImport],
        ["Verification Approver"] = [Permissions.EntityView, Permissions.VerificationApproveChange],
        ["Invoice Approver"] = [Permissions.EntityView, Permissions.InvoiceView, Permissions.InvoiceApprove],
        ["Workflow Administrator"] = [Permissions.EntityView, Permissions.WorkflowView, Permissions.WorkflowConfigure],
        ["Workflow Change Approver"] = [Permissions.EntityView, Permissions.WorkflowView, Permissions.WorkflowApproveChange],
        ["Approval Coordinator"] = [Permissions.EntityView, Permissions.InvoiceView, Permissions.WorkflowView, Permissions.WorkflowAdminister],
        ["Payment Preparer"] = [Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.PaymentView, Permissions.PaymentPrepare],
        ["Payment Approver"] = [Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.PaymentView, Permissions.PaymentApprove],
        ["ERP Administrator"] = [Permissions.EntityView, Permissions.ErpView, Permissions.ErpConfigure],
        ["ERP Change Approver"] = [Permissions.EntityView, Permissions.ErpView, Permissions.ErpApproveChange],
        ["ERP Operator"] = [Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.ErpView, Permissions.ErpOperate],
        ["Payment Releaser"] = [Permissions.EntityView, Permissions.VendorView, Permissions.InvoiceView, Permissions.PaymentView, Permissions.PaymentRelease, Permissions.PaymentInstructionDownload],
    };
}
