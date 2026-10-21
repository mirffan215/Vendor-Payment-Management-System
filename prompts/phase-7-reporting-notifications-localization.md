Read CLAUDE.md and /docs. Implement Phase 7 only. Show a plan first.

Scope:
- Reporting: payables aging, approval delays, cash/payment forecast, exceptions; export to Excel/PDF/CSV; tenant and entity scoped
- Audit evidence export (filtered, signed/hashed bundles)
- Notifications: email (Graph + transactional provider), in-app, Teams-ready webhook, mobile-ready push abstraction; user preferences and templates per tenant
- Localization: complete Arabic UI and RTL, Arabic document templates, Hijri/Gregorian dates, SAR and multi-currency formatting
- Saudi VAT handling and the ZATCA module, scoped to VPMS's actual role as documented in /docs; configurable and isolated

Acceptance: reports respect tenant isolation, Arabic/RTL works across all screens, ZATCA scope matches the documented decision.
Stop and summarise when done.
