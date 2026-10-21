Read CLAUDE.md. This is Phase 0: design only. Write NO application code.

Produce:
1. /docs/architecture.md: solution structure, bounded contexts, tenancy model, workflow-engine design,
   audit/immutability design, ERP adapter design, security model, notification and background-job design.
2. /docs/data-model.md: core entities and relationships (tenant, legal entity, vendor, vendor-entity link,
   bank details with change control, invoice, invoice lines, attachments, workflow definition/version/instance,
   approval task, delegation, payment request, payment proposal, release authorization, audit event).
3. /docs/adr/: one ADR per key decision (tenancy strategy, workflow engine, audit storage, encryption, ERP adapter).
4. /docs/roadmap.md: phases with acceptance criteria, matching the phases in /prompts.
5. /docs/risks-and-open-questions.md, including which ZATCA obligations depend on VPMS's role.

Use plan mode. Stop when finished and wait for my approval.
