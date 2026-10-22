using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.Invoices.Application;
using Vpms.Payments.Application;
using Vpms.Payments.Domain;
using Vpms.Payments.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PaymentRequestTests(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    [Fact]
    public async Task An_approved_invoice_becomes_a_payment_request_that_remembers_who_took_part()
    {
        var invoice = await ApprovedInvoice();
        var request = await RequestFor(invoice);

        Assert.Equal(RequestStatus.Created, request.Status);
        Assert.Equal(invoice.Total, request.Amount);
        Assert.Equal("SAR", request.CurrencyCode);
        Assert.Equal(1, request.InvoiceVersion);
        Assert.Equal(invoice.DueDate ?? invoice.InvoiceDate, request.DueDate);

        var row = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<PaymentsDbContext>().Requests.AsNoTracking().SingleAsync(r => r.Id == request.Id));
        Assert.Equal(BankDetailsId, row.BankDetailsId);
        Assert.Equivalent(new[] { Clerk, A1, BankMaker, BankChecker, Preparer }, row.Involved);
        Assert.Equal(1, await AuditCount("payment.request.created"));
    }

    [Fact]
    public async Task Only_payable_invoices_get_a_request_and_each_failure_has_a_stable_code()
    {
        var verified = await VerifiedInvoice(Clerk, "NOT-APPROVED");
        var approved = await ApprovedInvoice("OK-1");
        var unknown = Guid.NewGuid();

        var results = await Pay(Preparer, s => s.CreateAsync([verified.Id, approved.Id, unknown]));
        Assert.Equal("invoice_not_payable", results.Single(r => r.InvoiceId == verified.Id).ErrorCode);
        Assert.NotNull(results.Single(r => r.InvoiceId == approved.Id).Request);
        Assert.Equal("invoice_not_found", results.Single(r => r.InvoiceId == unknown).ErrorCode);
    }

    [Fact]
    public async Task An_invoice_has_at_most_one_open_request_and_a_cancelled_one_frees_it()
    {
        var invoice = await ApprovedInvoice();
        var first = await RequestFor(invoice);
        Assert.Equal("request_exists", (await Pay(Preparer, s => s.CreateAsync([invoice.Id]))).Single().ErrorCode);
        Assert.Equal("request_exists", (await Pay(Preparer, s => s.CreateAsync([invoice.Id, invoice.Id]))).Single().ErrorCode);

        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => Pay(Preparer, s => s.CancelAsync(first.Id, " ")))).Code);
        var cancelled = await Pay(Preparer, s => s.CancelAsync(first.Id, "paid another way"));
        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);

        Assert.NotNull((await Pay(Preparer, s => s.CreateAsync([invoice.Id]))).Single().Request);
        Assert.Equal(1, await AuditCount("payment.request.cancelled"));
    }

    [Fact]
    public async Task Without_verified_bank_details_in_the_invoice_currency_no_request_is_possible()
    {
        // a vendor with bank details only in USD, an invoice in SAR
        var vendor = await ActiveVendorKit.ActiveVendorAsync(W.Host.Services, W.TenantA, Entity, "Usd Only", Maker, Checker);
        await ActiveVendorKit.VerifiedBankDetailsAsync(W.Host.Services, W.TenantA, vendor, BankMaker, BankChecker, "USD", "AE070331234567890123456");
        var other = await W.AsAsync(W.TenantA, Clerk, async s =>
        {
            var data = new InvoiceData("NOBANK-1", TestDates.Ago(10), TestDates.Ago(10).AddDays(30), "SAR", 100, 15, 115, null, null, [new InvoiceLineInput("Work", 1, 100, 100, "S", 0.15m, 15, null, null)]);
            var created = await s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(Entity, vendor, data));
            return await s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id);
        });
        await Send(Clerk, other.Id);
        await Decide(A1, await TaskOf(A1));

        var result = (await Pay(Preparer, s => s.CreateAsync([other.Id]))).Single();
        Assert.Equal("no_verified_bank_details", result.ErrorCode);
        var listed = (await Pay(Preparer, s => s.ListPayableAsync(Entity, null))).Single(p => p.InvoiceId == other.Id);
        Assert.Equal(["no_verified_bank_details"], listed.Blockers);
    }

    [Fact]
    public async Task A_bank_change_waiting_for_a_decision_blocks_new_requests_for_that_vendor()
    {
        var invoice = await ApprovedInvoice();
        await W.AsAsync(W.TenantA, BankMaker, s => s.GetRequiredService<Vpms.Vendors.Application.IBankDetailsService>().ProposeAsync(Vendor,
            new Vpms.Vendors.Application.ProposeBankDetails("Al Amal Trading", "New Bank", null, "SA", "SAR", "SA4420000001234567891234", null, "Changed bank")));

        Assert.Equal("bank_change_pending", (await Pay(Preparer, s => s.CreateAsync([invoice.Id]))).Single().ErrorCode);
        Assert.Contains("bank_change_pending", (await Pay(Preparer, s => s.ListPayableAsync(Entity, null))).Single().Blockers);
    }

    [Fact]
    public async Task The_payable_list_shows_open_requests_and_hides_nothing_else()
    {
        var a = await ApprovedInvoice("A-1");
        var b = await ApprovedInvoice("B-1");
        await RequestFor(a);

        var list = await Pay(Preparer, s => s.ListPayableAsync(Entity, null));
        Assert.True(list.Single(p => p.InvoiceId == a.Id).HasOpenRequest);
        var open = list.Single(p => p.InvoiceId == b.Id);
        Assert.False(open.HasOpenRequest);
        Assert.Empty(open.Blockers);
        Assert.Empty(await Pay(Preparer, s => s.ListPayableAsync(null, new HashSet<Guid> { Guid.NewGuid() })));
    }

    [Fact]
    public async Task Requests_can_be_filtered_and_are_private_to_the_tenant()
    {
        var a = await ApprovedInvoice("A-1");
        var request = await RequestFor(a);
        Assert.Single(await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter("Created", Entity, Vendor), null, 0, 50)));
        Assert.Empty(await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter("Cancelled", null, null), null, 0, 50)));
        Assert.Empty(await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter(null, null, null), new HashSet<Guid> { Guid.NewGuid() }, 0, 50)));

        Assert.Empty(await W.AsAsync(W.TenantB, Preparer, s => s.GetRequiredService<IPaymentRequestService>().ListAsync(new PaymentRequestFilter(null, null, null), null, 0, 50)));
        Assert.Equal("invoice_not_found", (await W.AsAsync(W.TenantB, Preparer, s => s.GetRequiredService<IPaymentRequestService>().CreateAsync([a.Id]))).Single().ErrorCode);
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantB, Preparer, s => s.GetRequiredService<IPaymentRequestService>().CancelAsync(request.Id, "x")));
    }

    [Fact]
    public async Task A_changed_invoice_invalidates_its_request_and_after_re_approval_a_new_one_can_be_made()
    {
        var invoice = await ApprovedInvoice();
        var request = await RequestFor(invoice);

        var data = new InvoiceData(invoice.InvoiceNumber, invoice.InvoiceDate, invoice.DueDate, "SAR", 90, 13.5m, 103.5m, null, null,
            [new InvoiceLineInput("Work", 1, 90, 90, "S", 0.15m, 13.5m, null, null)]);
        await W.AsAsync(W.TenantA, Clerk, s => s.GetRequiredService<IInvoiceService>().ReviseAsync(invoice.Id, data, "discount applied"));

        var after = (await Pay(Preparer, s => s.ListAsync(new PaymentRequestFilter("Invalidated", null, null), null, 0, 10))).Single();
        Assert.Equal(request.Id, after.Id);
        Assert.Contains("revised", after.StatusReason);
        Assert.Equal("invoice_not_payable", (await Pay(Preparer, s => s.CreateAsync([invoice.Id]))).Single().ErrorCode);

        await Send(Clerk, invoice.Id);
        await Decide(A1, await TaskOf(A1));
        var again = await RequestFor(await Invoice(invoice.Id));
        Assert.Equal(103.5m, again.Amount);
        Assert.Equal(2, again.InvoiceVersion);
    }
}
