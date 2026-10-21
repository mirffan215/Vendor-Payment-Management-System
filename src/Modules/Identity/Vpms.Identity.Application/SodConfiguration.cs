using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Contracts;

namespace Vpms.Identity.Application;

public static class SodConfiguration
{
    /// <summary>Mandatory financial controls: these pairs can never be held by one person for the same scope.</summary>
    public static ConflictingPermissionsMatrix Default() => new(
    [
        new PermissionConflict(Permissions.InvoiceApprove, Permissions.PaymentRelease),
        new PermissionConflict(Permissions.VendorBankDetailsEdit, Permissions.VendorBankDetailsApprove),
        new PermissionConflict(Permissions.PaymentPrepare, Permissions.PaymentRelease),
        new PermissionConflict(Permissions.InvoiceCreate, Permissions.InvoiceApprove),
    ]);
}
