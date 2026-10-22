# ADR-0023: Step-up authentication, ending sessions, and security logging
Status: Accepted
Date: 2026-10-22

## Context
Staff sign in through their organisation's identity provider and VPMS keeps a cookie session (ADR-0006). A cookie proves that someone signed in
earlier, not that the person at the keyboard is the one who did. For actions that move or expose money, or change who may do what, that is not
enough (ASVS V2, V3). There was also no way to end a session other than waiting for it to expire or disabling the user, and no record of refusals.

## Decision
- **Step-up.** `[RequireStepUp]` on an action requires that the session's sign-in time is within `Auth:StepUpWindow` (default 15 minutes). Otherwise the
  answer is 403 with `errorCode: step_up_required` and `stepUpUrl: /auth/step-up`. That endpoint challenges the person's own identity provider with
  `prompt=login` and `max_age=0` and returns them to the page they were on. On return the token must carry an `auth_time` within two minutes; a
  provider that ignores the request fails the sign-in with `StepUpNotPerformed` and the session is unchanged. Applied to: bank-detail change approval and
  reveal, payment release and instruction download, role creation, change, deletion, assignment and revocation, identity-provider changes, ERP
  configuration approval and notification-channel approval.
- **Ending sessions.** `User.SessionsValidFrom`: a session whose sign-in time is at or before it is rejected. A person can "sign out everywhere"
  (`POST /session/revoke-all`) and an administrator with `User.Manage` can do it for anyone (`POST /admin/users/{id}/revoke-sessions`). Both are audited.
  The check reads a per-person state cached for one minute; the instance that handles the request forgets its copy at once, other instances within a minute.
- **Security events.** Refusals (403, 429, missing anti-forgery token, step-up required) are logged as warnings naming the route pattern and the person,
  never the query or body, and counted in the `vpms.security.events` metric by type. Background passes are counted and timed (`vpms.job.runs`,
  `vpms.job.duration`). Alerts in the deployment are written against these.
- **Log scrubbing.** Properties named like secrets are replaced, and every rendered line is scrubbed for IBANs, long digit runs (kept as `****1234`),
  bearer tokens, JWTs, `key=value` secrets and e-mail addresses. It is a safety net; logging such data on purpose remains a defect.

## Alternatives considered
- **Require MFA claims (`amr`/`acr`) from the identity provider.** Not built: it needs a per-provider setting and provider-specific claim values. Multi-factor
  sign-in therefore stays the identity provider's responsibility (recorded as a residual risk).
- **A server-side session store.** Gives per-session revocation and a device list, at the cost of a store on every request. "Sign out everywhere" covers the
  need seen so far.
- **Prompting for a password inside VPMS.** VPMS holds no staff passwords, by design.

## Consequences
- The identity provider must honour `prompt=login` and `max_age`. Microsoft Entra ID, Google and standards-compliant OIDC providers do; the check on
  `auth_time` makes a non-compliant provider fail visibly instead of silently skipping the check.
- A person can lose unsaved input when sent to the provider and back; the app shows the prompt before sending them.
- Revocation latency is up to one minute on other instances.
- A sign-in in the same second as a revocation is refused once (sign-in times are whole seconds); trying again works.
