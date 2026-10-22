# ADR-0015: Approval controls: segregation of duties, authority, delegation and escalation
Status: Accepted
Date: 2026-10-22

## Context
CLAUDE.md requires maker-checker, no self-approval, and independent approval and payment release. These must hold in the engine,
not only in screens, including when someone acts for somebody else.

## Decision
- **Makers.** Everyone who prepared the item (creator, last editor, the person who sent it for approval, and anyone who accepted
  a verification exception on it) is a maker. Makers are never assigned a task, `MakerCheckerGuard` rejects any decision by a
  maker or on behalf of one, and the refusal is audited (`sod.invoice-approval.blocked`). A person who edits an item that is
  already in approval becomes a maker and is removed from open tasks.
- **Different person.** One person never decides two steps of the same run, as themselves or as a delegate
  (`DifferentPersonRule` over actors and the people they stood in for). This also covers a person assigned to two parallel
  branches. Escalation never adds a maker or someone who already decided.
- **Permission at decision time.** The actor (and whoever they act for) must hold `Invoice.Approve` for the legal entity when
  deciding, not only when the task was created.
- **Authority limits fail closed.** An approval needs an active `ApprovalAuthority` row for the approver covering the amount in
  the item's currency, the date and any dimension scope. No row means no right to approve. There is no currency conversion: an
  approver in another currency needs a row in that currency. Rejecting and returning need no authority.
- **Delegation.** A delegation is per legal entity and date range (half-open, at most 180 days), with an optional amount cap. Both
  people must hold `Invoice.Approve` for the entity. The delegate acts with the delegator's authority, never more; the decision
  records both. There are no chains: the delegator must be an assignee of the task. Delegations can be revoked at once by the
  delegator, the delegate or an administrator. A delegate who is a maker can never decide.
- **Deadlines.** A task has a due time and an optional reminder. The worker sends each reminder once and escalates each overdue
  task once (uniqueness enforced). Escalation adds the step's escalation approver; one approval from them completes the step
  whatever its mode, because escalation exists to unblock. A step with no escalation approver only becomes overdue. If nobody
  eligible exists the run is flagged as needing attention instead of silently stalling.
- **Notifications** are in-app notices until the notification phase; the workflow only records them.
- **Role matrix.** Workflow configurers cannot approve workflow changes or invoices; invoice preparers cannot approve invoices
  (existing); invoice approvers cannot release payments (existing, enforced again in Phase 5).

## Consequences
- Setting up a tenant requires authority rows for approvers; this is deliberate.
- Escalation can weaken an All-mode step; it is a configured, audited choice per step.
