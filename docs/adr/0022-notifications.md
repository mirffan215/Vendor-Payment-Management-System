# ADR-0022: Notifications: sweep, outbox, channels and mandatory kinds
Status: Accepted
Date: 2026-10-22

## Context
Until Phase 7 the only notice was an in-app list of reminders and escalations. Approvers, treasury and administrators need to be told
when something needs them, by email and chat as well as in the app, without every module learning how to send mail.

## Decision
- **A `Notifications` module (schema `notify`)** with `Notification` (per user, unique dedupe key), `NotificationDelivery` (outbox per channel),
  `NotificationPreference`, `NotificationTemplate` (tenant overrides), `ChannelConfig` (versioned) and `NotificationCursor`.
- **Events are found by a sweep, not by hooks.** Source modules expose `INotificationSource` implementations over their contracts (approvals,
  invoice exceptions, bank changes, payment proposals, ERP dead letters, reconciliation differences). A worker job reads new events with an overlap
  window; the unique dedupe key makes a re-read harmless; one failing source does not stop the others. Recipients are resolved through
  `IApproverDirectory.ListPermissionHoldersAsync`, so only people who can act are told.
- **Deliveries are an outbox** with leases (RowVersion), exponential backoff, dead letter and a visible dead-letter list for administrators. The
  same pattern as the ERP queue (ADR-0019).
- **Channels:** in-app (bell and inbox); email through Microsoft Graph (`Mail.Send`) or generic SMTP (MailKit); Teams incoming webhook (https, allow-listed
  host suffixes and private-address refusal against SSRF, adaptive card); push as an abstraction with a logging provider only. All behind interfaces.
- **Mandatory kinds** (approval assigned/reminder/escalated, bank change pending) cannot be switched off by a person; preferences for other kinds are per
  channel. Mandatory in-app notices are always created.
- **Channel configuration is versioned and needs a second person** (it decides where company messages go). Only the name of a secret is stored, read through
  `ISecretReader`.
- **Templates** have fixed placeholders and no logic; values are escaped; English and Arabic defaults are in code, tenants may override. The recipient's stored
  language (Identity) picks the template.

## Alternatives considered
- **Publishing events from inside domain code:** couples every module to notification delivery and loses events on a crash. Rejected.
- **A template language with expressions:** a code-execution risk in tenant-edited text. Rejected.
- **Digest emails:** a flood-control option, deliberately not built; dedupe keys are the only brake today.

## Consequences
- Delivery is at-least-once with dedupe; a lost worker only delays messages.
- Graph `Mail.Send`, real SMTP servers and Teams webhooks have not been exercised live. Tests use in-process stub servers; see the Phase 7 summary.
- Mobile push has no device registration; nothing is sent until a provider and registration exist.
- The earlier `WorkflowNotice` list still exists beside the new inbox and should be folded into it later.
