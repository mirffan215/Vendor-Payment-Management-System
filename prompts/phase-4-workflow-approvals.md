Read CLAUDE.md and /docs. Implement Phase 4 only. Show a plan first (use plan mode).

Scope:
- Versioned, data-driven workflow engine: sequential, parallel, conditional, multilevel steps
- Approval matrices per legal entity (amount, vendor, cost dimension conditions)
- In-flight instances stay on the version they started with
- Maker-checker enforcement and self-approval prevention in the engine
- Delegation (substitute approvers, date ranges), deadlines, reminders, escalation via durable background jobs
- Re-approval triggers when approved invoice data changes materially (configurable materiality)
- Approver UI: inbox, approve/reject/return, comments, full audit trail

Acceptance: simulation tests for each workflow pattern, self-approval impossible, delegation and escalation tested.
Stop and summarise when done.
