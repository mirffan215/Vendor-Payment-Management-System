using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Application;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;
using Vpms.Vendors.Application;
using Vpms.Vendors.Domain;

namespace Vpms.TestSupport;

/// <summary>
/// Builds the master data invoice tests need (legal entity, an Active vendor linked to it) through the real services,
/// including the two-person vendor approval. Shared by the integration and API test projects.
/// </summary>
public static class ActiveVendorKit
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");

    public static async Task<T> AsAsync<T>(IServiceProvider root, Guid tenant, Guid actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = root.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
        audit.ActorId = actor;
        audit.ActorType = AuditActorType.User;
        audit.CorrelationId = "kit-" + Guid.NewGuid().ToString("N")[..8];
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        return await action(scope.ServiceProvider);
    }

    public static Task<Guid> CreateEntityAsync(IServiceProvider root, Guid tenant, Guid actor, string code, string country = "SA", string currency = "SAR") =>
        AsAsync(root, tenant, actor, async s =>
            (await s.GetRequiredService<ILegalEntityService>().CreateAsync(new CreateLegalEntity(code, code, null, country, currency, null))).Id);

    /// <summary>An Active vendor linked to the entity (default currency SAR). Needs two different user ids: maker and checker.</summary>
    public static async Task<Guid> ActiveVendorAsync(IServiceProvider root, Guid tenant, Guid entityId, string name, Guid maker, Guid checker, string currency = "SAR")
    {
        var vendor = await AsAsync(root, tenant, maker, async s =>
            await s.GetRequiredService<IVendorService>().CreateAsync(new CreateVendor(name, null, null, "SA", null, null, null, null)));
        var docs = new List<VendorDocumentDto>();
        foreach (var type in VendorDocumentType.RequiredForApproval)
        {
            docs.Add(await AsAsync(root, tenant, maker, async s =>
            {
                var upload = await s.GetRequiredService<IAttachmentService>()
                    .UploadAsync(new UploadRequest("Vendor", vendor.Id.ToString(), null, "doc.pdf", new MemoryStream(Pdf), maker, UploaderKind.Staff));
                return await s.GetRequiredService<IVendorDocumentService>().AddAsync(vendor.Id, type, upload.Id, null);
            }));
        }

        await ScanAllAsync(root);
        await AsAsync(root, tenant, maker, s => s.GetRequiredService<IVendorService>().SubmitAsync(vendor.Id));
        await AsAsync(root, tenant, checker, s => s.GetRequiredService<IVendorService>().StartReviewAsync(vendor.Id));
        foreach (var d in docs) await AsAsync(root, tenant, checker, s => s.GetRequiredService<IVendorDocumentService>().VerifyAsync(d.Id));
        await AsAsync(root, tenant, checker, s => s.GetRequiredService<IVendorService>().ApproveAsync(vendor.Id));
        await AsAsync(root, tenant, maker, s => s.GetRequiredService<IVendorEntityLinkService>()
            .SaveAsync(vendor.Id, entityId, new SaveVendorEntityLink(null, null, currency, null, null, null)));
        return vendor.Id;
    }

    /// <summary>A random but valid Saudi IBAN (correct mod-97 check digits).</summary>
    public static string NewIban()
    {
        var bban = string.Concat(Enumerable.Range(0, 20).Select(_ => Random.Shared.Next(10)));
        var rearranged = bban + "2810" + "00"; // S=28, A=10, check digits 00
        var remainder = 0;
        foreach (var ch in rearranged) remainder = (remainder * 10 + (ch - '0')) % 97;
        return "SA" + (98 - remainder).ToString("D2") + bban;
    }

    /// <summary>Verified bank details for the vendor in a currency, through the real two-person change (maker proposes, checker approves).</summary>
    public static async Task<Guid> VerifiedBankDetailsAsync(IServiceProvider root, Guid tenant, Guid vendorId, Guid maker, Guid checker, string currency = "SAR", string? iban = null)
    {
        iban ??= NewIban(); // a fresh account per vendor: shared accounts are flagged as possible fraud
        var change = await AsAsync(root, tenant, maker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(vendorId,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "Example Bank", "EXMPSARI", iban[..2], currency, iban, null, "Initial bank details")));
        await AsAsync(root, tenant, checker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ApproveAsync(change.Id, null, "checked"));
        return change.ProposedBankDetailsId;
    }

    /// <summary>Links an existing active vendor to another entity.</summary>
    public static Task LinkAsync(IServiceProvider root, Guid tenant, Guid actor, Guid vendorId, Guid entityId, string currency = "SAR") =>
        AsAsync(root, tenant, actor, s => s.GetRequiredService<IVendorEntityLinkService>()
            .SaveAsync(vendorId, entityId, new SaveVendorEntityLink(null, null, currency, null, null, null)));

    public static async Task ScanAllAsync(IServiceProvider root)
    {
        IReadOnlyList<PendingScan> pending;
        await using (var list = root.CreateAsyncScope())
        {
            pending = await list.ServiceProvider.GetRequiredService<IAttachmentScanService>().ListPendingAsync(200);
        }

        foreach (var item in pending)
        {
            await using var scope = root.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(item.TenantId);
            await scope.ServiceProvider.GetRequiredService<IAttachmentScanService>().ScanAsync(item.AttachmentId);
        }
    }
}

/// <summary>Dates relative to now, so tests do not start failing the "invoice too old" check as time passes.</summary>
public static class TestDates
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public static DateOnly Ago(int days) => Today.AddDays(-days);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
}
