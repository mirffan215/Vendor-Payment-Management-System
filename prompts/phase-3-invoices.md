Read CLAUDE.md and /docs. Implement Phase 3 only. Show a plan first.

Scope:
- Invoice intake: manual entry, vendor portal upload, email intake (Microsoft Graph), document upload; OCR-ready pipeline interface
- Invoice header, lines, attachments, status lifecycle
- Verification engine: duplicate detection (vendor + number + amount + date rules, fuzzy option), PO matching,
  tax and amount validation, configurable per legal entity
- Exceptions queue for failed validations
- Tax handling designed for VAT with Saudi rules as a pluggable module

Acceptance: duplicates are blocked or flagged, validation rules are configurable per entity, full test coverage of rules.
Stop and summarise when done.
