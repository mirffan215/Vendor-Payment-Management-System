using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.Invoices.Domain;
using Vpms.Workflow.Contracts;

namespace Vpms.Invoices.Infrastructure;

/// <summary>Applies the end of an approval run to its invoice. Idempotent: the workflow sweep may deliver the same outcome again.</summary>
public sealed class InvoiceApprovalOutcomeHandler(InvoicesDbContext db, IAuditPort audit) : IApprovalOutcomeHandler
{
    public string SubjectType => "Invoice";

    public async Task ApplyAsync(ApprovalOutcome outcome, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == outcome.SubjectId, ct);
        if (invoice is null || invoice.ApprovalRunId != outcome.RunId)
        {
            // A newer run (after a restart) or a different invoice owns the state now; this outcome no longer applies.
            audit.Record(new AuditEntry("invoice.approval-outcome.ignored", "Invoice", outcome.SubjectId.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { outcome.RunId, outcome.Outcome, reason = invoice is null ? "unknown invoice" : "superseded run" })));
            await db.SaveChangesAsync(ct);
            return;
        }

        var before = invoice.Status;
        try
        {
            switch (outcome.Outcome)
            {
                case ApprovalOutcomes.Approved: invoice.ApplyApproved(outcome.SubjectVersion, outcome.At); break;
                case ApprovalOutcomes.Rejected: invoice.ApplyRejectedByApproval(outcome.Reason); break;
                case ApprovalOutcomes.Returned: invoice.ApplyReturnedByApproval(outcome.Reason); break;
                default: return; // Cancelled runs are handled by the caller that cancelled them
            }
        }
        catch (ConflictException)
        {
            // The invoice is no longer in the state this outcome expects (for example it was cancelled meanwhile).
            audit.Record(new AuditEntry("invoice.approval-outcome.ignored", "Invoice", invoice.Id.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { outcome.RunId, outcome.Outcome, status = before })));
            await db.SaveChangesAsync(ct);
            return;
        }

        if (invoice.Status != before)
        {
            audit.Record(new AuditEntry($"invoice.{outcome.Outcome.ToLowerInvariant()}-by-approval", "Invoice", invoice.Id.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { from = before, to = invoice.Status, outcome.RunId, outcome.Reason })));
        }

        await db.SaveChangesAsync(ct);
    }
}
