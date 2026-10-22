namespace Vpms.Payments.Domain;

public sealed record SeparationFinding(string Code);

/// <summary>
/// Who may release a proposal. The person releasing money must be a different person from everyone who prepared, submitted or approved the
/// proposal, and from everyone who took part in the invoices (prepared, let through verification, approved) or in the bank details
/// (proposed or checked) of any item.
/// </summary>
public static class ReleaseSeparation
{
    public static IReadOnlyList<SeparationFinding> Evaluate(Guid releaser, PaymentProposal proposal, IEnumerable<PaymentRequest> requests)
    {
        var findings = new List<SeparationFinding>();
        if (releaser == proposal.CreatedBy) findings.Add(new SeparationFinding("releaser_is_maker"));
        if (releaser == proposal.SubmittedBy) findings.Add(new SeparationFinding("releaser_is_submitter"));
        if (releaser == proposal.ApprovedBy) findings.Add(new SeparationFinding("releaser_is_approver"));
        if (requests.Any(r => r.Involved.Contains(releaser))) findings.Add(new SeparationFinding("releaser_took_part_in_invoice_or_bank_details"));
        return findings;
    }
}
