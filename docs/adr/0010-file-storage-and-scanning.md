# ADR-0010: File storage and virus scanning
Status: Accepted
Date: 2026-10-21

## Context
Vendors and staff upload documents (and later invoices). Files must stay tenant-isolated, backends must be portable
(Azure-first, S3-compatible), and unscanned files must never be served.

## Decision
- `IBlobBackend` (in-memory, Azure Blob, S3-compatible; write-once keys) sits behind `TenantScopedFileStore`, the only
  entry point, which prefixes every key with the tenant and rejects traversal. The provider is configuration.
- The `Documents` module owns `Attachment` records: owner type/id, name, content type, size, SHA-256, scan status.
  Uploads are validated (size, extension allow-list, magic bytes, sanitised names) and stored in **quarantine**
  (`Pending`). A scanner (`IVirusScanner`: EICAR for tests and development, ClamAV INSTREAM adapter) moves them to
  `Clean`, `Infected` (content deleted) or `Failed`. Only `Clean` files can be downloaded; downloads are audited.
- Scanning runs in the Worker (`AttachmentScanJob`); upload never waits for it.
- Owning modules authorise access to the owner (for example a vendor session may only reach its own vendor's
  documents); the attachment service does not decide business access.
- Both backends run the same contract suite against emulators (Azurite, an in-process fake S3; MinIO in CI).

## Alternatives considered
- Scan inline at upload: blocks requests and couples availability to the scanner.
- Serve files first, scan later: rejected; a malicious file could be fetched before the scan.

## Consequences
- Users wait for the scan before using a file; the UI shows the scan state.
- Retention, legal hold and immutability policies for stored files are Phase 8.
- Content-type sniffing is limited to magic bytes of allowed types; a second engine can be added behind `IVirusScanner`.
