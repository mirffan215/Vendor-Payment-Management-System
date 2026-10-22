# Phase 4 summary: workflow and approvals

Status: complete, awaiting your approval. Phase 5 has not been started.

## What was built
- **Workflow engine** (new module `Workflow`): versioned, data-driven definitions with approval steps (any one / all / quorum),
  parallel steps (join on all or any), condition steps (amount, currency, vendor, source, dimensions) and multilevel chains; a
  validator; a pure run state machine; a simulator used by the tests and by the "try it with an invoice" panel. ADR-0014.
- **Approval matrix per legal entity:** the Phase 2 matrix rules compile to a workflow. Every new workflow version needs a second
  person to approve it. Runs stay on the version they started with.
- **Controls enforced in the engine:** makers (creator, last editor, sender, people who accepted a verification exception) never
  get a task and can never decide; one person never decides two steps of a run; approval permission checked at decision time;
  approval authority (amount, currency, dates, dimension scope) checked and failing closed. Every refusal is audited. ADR-0015.
- **Delegation:** per legal entity and period, optional amount cap, acts with the delegator's authority, never on an invoice the
  delegate prepared, no chains, revocable at once, recorded on every decision.
- **Deadlines:** one reminder and one escalation per task through a durable worker job; the escalation approver can complete the
  step; stalled steps are flagged. In-app notices only.
- **Re-approval on material change:** per-entity materiality settings (second-person approved), compared with the state when
  approval started so small edits cannot add up. A material revision cancels the run and sends the invoice back through
  verification; a small one keeps the approval; the editor can no longer approve. ADR-0016.
- **Invoice lifecycle:** InApproval, Approved, Returned added. Outcomes are applied idempotently, with a retry sweep.
- **UI (English/Arabic, RTL):** approver inbox and notices, task page (approve / return / reject with comments, history including
  decisions made for others), approval history and send/revise on the invoice, delegations, workflow and materiality administration
  with validation, simulation and a change inbox.

## Acceptance evidence
| Requirement | Evidence |
|---|---|
| Simulation test for each pattern | `WorkflowPatternTests`: sequential, parallel join all / any, nested parallel, conditional (boundaries, diamond), multilevel, AnyOne / All / Quorum, role approvers, reject / return, deadlines |
| Self-approval impossible | `WorkflowControlsTests` (maker, every preparer, acting for an approver, acting for the maker, two branches, two levels, same task twice); `ApprovalTests` and `DelegationTests` through the real database, permissions and audit |
| Delegation tested | `DelegationTests`: window edges (end exclusive), revoke, cap, delegator authority, maker as delegate, no chains, overlap, validation, tenant isolation |
| Escalation tested | `DeadlineTests` and `EscalationTests` with a fake clock: reminder once, escalation once, escalation approver completes an All step, makers excluded, no-escalation overdue, scan |
| Version pinning | `ApprovalTests.A_run_that_started_on_one_version_finishes_on_it...` |
| Re-approval | `RevisionTests` and `MaterialityTests`: allowances at their edges, cumulative drift, approved and in-approval invoices, editor becomes a maker |

Local results: unit 488, architecture 9, integration 318 (5 skipped, the same MinIO tests), API 108, web Vitest 65, Playwright
(new approvals specs) 3; Release build with 0 warnings.

## Decisions that differ from or extend the plan
- **Every** workflow change and materiality change needs a second person (not only loosening ones): deciding which routing is
  "looser" is not reliably computable.
- Approval authority is **always** enforced; an approver with no authority row cannot approve. There is no currency conversion.
- Escalation adds an approver whose single approval completes the step (a deliberate, audited choice per step).
- Matrix rule JSON now has a defined shape (documented in `MatrixCompiler`).
- Notices are in-app only; email and Teams arrive with the notification phase.

## Known limitations and follow-ups
- Invoice and workflow are separate transactions; the outcome sweep makes them consistent (delay of up to the sweep interval).
- Dimensions used for routing come from invoice lines; if lines disagree for a dimension the value is "mixed" and matches no condition.
- No manager-chain approver type (there is no org hierarchy yet); approvers are users or roles.
- No approval by email link or mobile push; the UI is the only place to decide.
- Payment release (Phase 5) must check `Approved` and `ApprovedVersion == Version`, and that the releaser is not an approver of the invoice.
- Earlier-phase items unchanged: Graph not tested live, no OCR engine, MinIO / Docker image builds, anchoring, key ring.

## Open items for you
1. Approve Phase 4, or list changes.
2. Confirm that every workflow and materiality change needs a second person.
3. Confirm "no authority row = cannot approve" for onboarding customers (authority rows must be created for approvers).
4. Confirm that escalation approvers may complete a step alone.
5. Say "start Phase 5" when ready; I will show a plan first.
