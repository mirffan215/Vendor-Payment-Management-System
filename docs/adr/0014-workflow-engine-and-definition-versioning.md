# ADR-0014: Workflow engine and definition versioning
Status: Accepted
Date: 2026-10-22

Builds on ADR-0002 (own data-driven engine).

## Context
Approval routing decides who can release money. It must be explainable afterwards, identical for an item for its whole life,
and not changeable by one person. The engine must stay small enough to test exhaustively (risk R10).

## Decision
- **A small closed model.** A definition is a graph of three step types: `Approval` (approvers, mode AnyOne / All / Quorum,
  optional deadline, reminder and escalation approver), `Parallel` (branches joined on All or Any) and `Condition` (ordered
  cases, each a list of clauses on amount, currency, vendor, source or a financial dimension; operators eq, ne, gt, ge, lt, le,
  in). There is no scripting. A validator rejects cycles, unreachable steps, overlapping branches, bad approvers, bad quorums and
  inconsistent deadlines before a definition can be stored.
- **No match is an error, not an approval.** A condition with no matching case and no default stops with `no_route`; ending a
  path must be written explicitly (`$end`). An entity without an approved workflow cannot start approvals.
- **The run is a pure state machine** (`WorkflowRun`): it owns tasks, decisions and parallel joins and reports a final status.
  It does no I/O, so every pattern is tested by simulation (`WorkflowSimulator`) with scripted decisions.
- **Versions are immutable and every new version needs a second person.** A definition belongs to a legal entity and subject
  type; saving creates the next version as Pending; a different person with `Workflow.ApproveChange` makes it Active and the
  previous version Superseded (one Active and one Pending per entity, database-enforced). Configure and approve cannot be held by
  one person. We approve every change, not only loosening ones, because deciding which routing change is looser than another is
  not reliably computable.
- **Runs are pinned.** A run stores the definition id, version and hash it started with and always reads that version; changing
  the workflow never changes items in flight.
- **Approval matrix.** The customer-admin approval matrix rules (priority, conditions on amount, currency, vendor, dimensions;
  levels with approvers, mode and deadlines) compile to a definition: a condition step tries rules in priority order, each rule
  becomes a chain of approval levels. The compiled definition goes through the same versioning and approval.
- **Approvers.** A definition names users or roles. A role means its current holders for the legal entity at the moment a task is
  created. Only people holding `Invoice.Approve` for the entity are ever assigned, whatever the definition says.
- **Subject-agnostic.** The engine knows a subject type, id, version, amount, currency, vendor, source, dimensions and makers.
  Invoices start approvals through `IApprovalStarter` and receive results through `IApprovalOutcomeHandler` (both in the
  workflow contracts); the engine references no subject module (architecture test). Payment requests in Phase 5 reuse it.
- **Outcomes are applied idempotently.** The decision is committed first; the subject module is then told. If that fails, a
  worker sweep repeats it. Stale outcomes (a newer run owns the subject, or its state moved on) are ignored and audited.

## Consequences
- Anything not expressible in the closed model needs a new step or clause type and an ADR.
- Two databases transactions (workflow, invoices) are not atomic; the sweep makes them eventually consistent and safe.
- Workflow changes are slower (two people) by design.
