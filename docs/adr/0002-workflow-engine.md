# ADR-0002: Workflow engine
Status: Proposed
Date: 2026-10-21

## Context
Approvals need multilevel, conditional, sequential and parallel routing, configurable per legal entity, with
delegation and escalation. In-flight items must not change behaviour when a definition is edited. Maker-checker and
self-approval prevention are mandatory. The tech stack fixes "own versioned, data-driven workflow engine".

## Decision
Build a small in-house engine inside the Approval Workflow module:
- Definitions are JSON graphs validated against a versioned schema; published versions are immutable
  (content hash stored). Editing creates a draft; publishing creates version N+1.
- An instance pins `VersionId` at start and never migrates.
- Step set: Approval, Sequential, Parallel (all/any/n-of-m), Condition, MultiLevel (approval matrix), Notify, Timer.
- Conditions use a restricted, side-effect-free expression language over a typed context (amount, currency,
  vendor, entity, dimensions); no arbitrary code.
- Engine state transitions are persisted transactionally with an append-only history, and audited.
- Guards (self-approval, maker-checker, authority, duplicate approver) live in the engine, not the UI.
- Time-based behaviour uses persisted timers processed by Worker Services.
- A simulation harness with a fake clock tests every pattern.

## Alternatives considered
- Elsa / Workflow Core / Temporal: faster start, but heavy dependency, versioning semantics and tenant isolation
  would need adapting, and approval-authority guards still need custom code.
- Hard-coded workflows: cannot meet per-entity configurability.

## Consequences
- Full control over auditability, versioning and SoD guards; cost is building and maintaining the engine.
- Keep the step set small and the definition schema stable; schema changes are versioned.
- Re-approval on material change cancels the instance and starts a new one on the current published version.
