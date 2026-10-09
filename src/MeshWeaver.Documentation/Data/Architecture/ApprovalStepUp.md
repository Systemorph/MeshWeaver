---
Name: Approval Step-Up
Category: Architecture
Description: Every approval act re-proves presence before it counts — Entra ID step-up (prompt=login + a Conditional Access authentication context with a phishing-resistant strength) for Microsoft accounts, the portal's own passkey for every other account, TOTP only where no passkey is possible. All three mint ONE sealed, single-use receipt bound to (user, action path, plan/script hash, expiry) that every approval path consumes server-side. Declared per deployment record, off until declared.
Icon: /static/NodeTypeIcons/key.svg
---

# Approval Step-Up

> **An approval is credential-equivalent — an approved instance action is a job that mints an admin
> kubeconfig. So no approval counts on a signed-in session alone: the approver re-proves presence
> for THAT action, and the server — never the page — checks the proof before it acts.** Policy
> `approval-step-up` ([register](../PolicyNotProse)).

GitHub calls it *sudo mode*. Here it is a **step-up receipt**: a short-lived, single-use record
minted only after a fresh, strong authentication, and bound to exactly what is being approved.
Every approval path consumes one, and refuses — by parking the approval, never by silently
proceeding — when there is none.

## The ladder — always prefer a passkey

Policy `approval-step-up` fixes the order: *always prefer a passkey, use a second factor only where
no passkey is available.*

| The approver signed in with… | Step-up method (`method` on the receipt) | Why |
|---|---|---|
| a Microsoft (Entra ID) account | **`entra`** — an OIDC round trip with `prompt=login` and the declared Conditional Access authentication context, whose policy requires the *Phishing-resistant MFA* authentication strength | The tenant already governs these accounts and their passkeys (FIDO2 / Windows Hello / Authenticator passkeys); one prompt, the tenant's own policy, nothing duplicated |
| any other account (Google, LinkedIn, Apple, GitHub) | **`passkey`** — the portal's own WebAuthn assertion | These providers cannot step up (measured below), so the portal does it itself |
| any other account, on a device that can do no passkey and with no passkey enrolled | **`totp`** — the portal's own RFC 6238 code | The last rung, offered only when the passkey rung is impossible |

An Entra account is never sent through the portal's own passkey as well — one prompt, not two.
The receipt records the method, so a later policy can require `entra`/`passkey` only for the
highest-risk actions without changing the receipt.

### Why the portal runs its own passkey for non-Microsoft accounts (measured)

The discovery documents, read 2026-10-09:

- **Google** (`accounts.google.com/.well-known/openid-configuration`) supports `prompt` and
  `max_age` re-authentication and returns `auth_time`, but advertises **no `acr_values_supported`
  and no `amr` claim** (`claims_supported` = aud, email, email_verified, exp, family_name,
  given_name, iat, iss, name, picture, sub). A Google re-login therefore proves *a* fresh sign-in,
  never *how* — a password re-entry and a passkey are indistinguishable to us.
- **LinkedIn** (`www.linkedin.com/oauth/.well-known/openid-configuration`) advertises no `prompt`,
  no `max_age`, no `auth_time`, no `acr`, no `amr` — there is no step-up to ask it for.
- **Entra ID** advertises `auth_time` and `acr`, and issues `acrs` when the requested
  authentication context's Conditional Access policy was satisfied.

So only Entra can attest *phishing-resistant, just now*. For everyone else the portal holds the
authenticator itself.

## The receipt

One shape for every method and every consumer.

| Field | Meaning |
|---|---|
| `Id` | random, 128 bits, hex |
| `UserId` | the mesh user id of the approver (`AccessContext.ObjectId`) — never the email |
| `Method` | `entra` · `passkey` · `totp` — an open vocabulary (`StepUpMethod`) |
| `Targets` | the actions it covers: `{ ActionPath, Binding }` each — the node path being approved and the hash of what was shown (InstanceAction plan digest, OperationRequest script hash, Activity content hash). A bulk approval is ONE step-up covering N targets |
| `IssuedAt` · `ExpiresAt` | `ExpiresAt = IssuedAt + ReceiptLifetime` (default 5 min) |
| `AuthenticatedAt` | when the user actually authenticated (`auth_time` for Entra, the assertion time for a passkey, the code time for TOTP) |
| `Evidence` | what was verified, for the audit line (`acrs=c1 amr=fido tid=…`, the credential id, …) — never a secret |
| `Seal` | HMAC-SHA256 over every field above, keyed from the instance master key (HKDF, purpose `MeshWeaver.StepUp.Receipt.v1`) |

**Where it lives:** `Auth/_StepUp/{id}`, written as System by the step-up endpoint only. The node
type's access rule admits System alone for every operation; the seal makes even a write that
bypassed it worthless. The seal's material is canonical — every field length-prefixed, instants as
UTC ticks, the targets counted — so no two receipts share bytes.

**Only the platform mints.** The public `IStepUpService` is check-and-consume only; minting lives on
the internal `StepUpService`, visible to the portal host that carries the step-up endpoints and to
nothing compiled in the mesh — otherwise any code able to resolve it could stamp itself a valid
receipt and skip the authentication it stands for.

**Single use:** consuming target *T* of receipt *R* CREATES `Auth/_StepUpUse/{R}-{key(T)}` as
System. Creation is atomic at the owning hub, so a second consumer's create fails — a replay is
refused by the store itself, not by a field someone could reset.

### How a receipt reaches the approval it covers

The step-up endpoint **stamps** the receipt id onto each target node, as the approver, under the
content property `stepUpReceipts` — a map `{ userId → receiptId }`, so several signers of one
activity each carry their own. The stamp is an ordinary `GetMeshNodeStream(path).Update(...)`; it
changes the node, so the owning watcher re-evaluates the approval with the receipt now present.

A consumer never looks a receipt up by a computed id: reading an absent node is a framework
defect (the routing not-found opens the storm-breaker on that path). The receipt is read only once
a stamp names it, and by then it exists.

### The verdict — one function, every consumer

`IStepUpService.Consume(receiptId, approver, actionPath, binding)` answers one `StepUpVerdict`,
checked in this order:

| Outcome | When |
|---|---|
| `NotRequired` | step-up is not enabled on this instance (policy off) |
| `Missing` | no receipt is stamped for this approver, or the stamped id does not resolve |
| `Unavailable` | the receipt read did not answer — fail closed, never read as "absent" |
| `Invalid` | the seal does not verify (tampered, or another instance's key) |
| `WrongUser` | the receipt belongs to someone else |
| `WrongAction` | no target names this action path |
| `WrongBinding` | the target's hash is not the hash being approved — the plan or script changed since the approver saw it |
| `Expired` | past `ExpiresAt` |
| `Replayed` | the consumption marker already exists |
| `Accepted` | consumed now; carries the method |

Anything but `Accepted`/`NotRequired` **parks** the approval with a localized reason and a
*Confirm with step-up* button; it is never treated as a refusal of the request itself, and never
as a pass. Consumption is the LAST check before the action proceeds, so a receipt is not burned by
an approval some other gate parks.

## Enablement — declared per deployment record, off until declared

| Portal configuration key | Deployment record field (`SignIn.StepUp`) | Default | Meaning |
|---|---|---|---|
| `Authentication:StepUp:Enabled` | `Enabled` | `false` | Every approval requires a receipt. Off ⇒ every consumer answers `NotRequired` |
| `Authentication:StepUp:Entra:AuthenticationContext` | `EntraAuthenticationContext` | — | The Conditional Access authentication context id (`c1`…`c99`) requested for Microsoft accounts. Enabled without it ⇒ Microsoft accounts are refused with *"step-up is not configured"*, never waved through |
| `Authentication:StepUp:Entra:TenantId` | `EntraTenantId` | `Authentication:Microsoft:TenantId` | The tenant whose `tid` the step-up token must carry. Required when the sign-in tenant is `common`/`organizations` |
| `Authentication:StepUp:Entra:RequireAmr` | `EntraRequireAmr` | `true` | Require an `amr` claim naming a phishing-resistant method. `false` accepts `acrs` alone — only the Conditional Access policy behind the context then vouches for the strength |
| `Authentication:StepUp:Entra:PhishingResistantAmr` | `EntraPhishingResistantAmr` | `fido,hwk` | The `amr` values that count as phishing-resistant (comma-separated). Not `ngcmfa` (Entra also emits it for an Authenticator push) and not `x509` (single-factor certificate) |
| `Authentication:StepUp:MaxAuthAgeSeconds` | `MaxAuthAgeSeconds` | `120` | How old `auth_time` may be when the token arrives |
| `Authentication:StepUp:ReceiptLifetimeSeconds` | `ReceiptLifetimeSeconds` | `300` | How long a receipt stays consumable |
| `Authentication:StepUp:AllowTotpFallback` | `AllowTotpFallback` | `true` | Whether the TOTP rung exists at all |

Step-up also needs the instance master key (`IMasterKeyProvider`) — it keys the seal. An instance
without one refuses to mint, naming the missing key.

## Switching it on for an Entra tenant — the admin steps

Done once per tenant by a **Conditional Access Administrator** (Entra ID P1 or higher). Nothing in
the portal changes behaviour until the last step.

1. **Create the authentication context.** Entra admin center → *Protection* → *Conditional Access*
   → *Authentication contexts* → *New authentication context*. Name `MeshWeaver approval`, ID
   `c1` (any free `c1`–`c99`), tick **Publish to apps**. Note the ID.
2. **Make sure a phishing-resistant method is enabled.** *Protection* → *Authentication methods*
   → *Policies* → enable **Passkey (FIDO2)** (and/or Windows Hello for Business, certificate-based
   authentication) for the approvers. Each approver registers one at
   `https://mysignins.microsoft.com/security-info`.
3. **Create the Conditional Access policy.** *Conditional Access* → *Policies* → *New policy*:
   - *Users*: include the approvers (e.g. the platform-admin group); exclude your break-glass accounts.
   - *Target resources*: choose **Authentication context** → select `c1`.
   - *Grant*: **Require authentication strength** → **Phishing-resistant MFA**.
   - *Session*: **Sign-in frequency** → **Every time**.
   - *Enable policy*: **Report-only** first, check the sign-in logs, then **On**.
4. **Register the callback and emit the claims.** App registrations → the portal's sign-in app (the
   `Authentication:Microsoft:ClientId`):
   - *Authentication* → *Web* → *Redirect URIs* → add `https://<portal host>/auth/step-up/callback`
     (next to the existing `/signin-microsoft`).
   - *Token configuration* → *Add optional claim* → **ID** → tick **`acrs`**, **`auth_time`** and
     **`amr`**. All three are v2.0 *optional* ID-token claims: without `auth_time` every step-up
     fails `auth_time`, and without `amr` it fails `amr` (the default `RequireAmr = true`).
5. **Declare it on the deployment record** (control instance, `Deployments/<name>`):
   `SignIn.StepUp.EntraAuthenticationContext = "c1"`, then `SignIn.StepUp.Enabled = true`, and roll.

🚨 Without step 3, Entra issues the `acrs` claim for an *unprotected* context to anyone who signs
in — Microsoft's own table: *"ACRS requested, no policy assigned → ACRS added to claims"*. The
`acrs` check is only as strong as the policy behind it, which is why the portal ALSO requires an
`amr` naming a phishing-resistant method (`fido` for a FIDO2 key or a passkey, `hwk` for Windows
Hello for Business) — Microsoft's AMR table maps an Authenticator push to `rsa, ngcmfa, mfa`, a
password to `pwd`, so neither passes.

## The Entra rung — exactly what the server checks

`GET /auth/step-up?target={path}&binding={hash}[&target=…&binding=…]&returnUrl={local}`

1. Signed in, step-up enabled, the account signed in through the `Microsoft` scheme — the session's
   `mw_idp` claim, set from the sign-in TICKET the challenged scheme produced, never from the
   callback route. A session from before that claim existed is asked to sign in again — the
   provider is never guessed.
2. The pending step-up — state, nonce, the targets, the return URL, the user, ten minutes — is
   stored SERVER-side at `Auth/_StepUpPending/{handle}` (System-only); the browser carries only the
   handle and the state, sealed with Data Protection (a bulk approval's targets would overflow a
   cookie). The callback reads it once and deletes it.
3. Redirect to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize` with
   `prompt=login`, `login_hint` = the signed-in account, a fresh `nonce`, and
   `claims={"id_token":{"acrs":{"essential":true,"value":"c1"}}}`.

`GET /auth/step-up/callback?code&state` exchanges the code at the token endpoint and validates the
`id_token`:

| Check | Refused when |
|---|---|
| signature | not signed by a key in the tenant's published JWKS |
| `iss` / `tid` | not `https://login.microsoftonline.com/{tid}/v2.0` for the configured step-up tenant |
| `aud` | not the portal's client id |
| `exp` / `nbf` | outside the token's lifetime |
| `nonce` | not the nonce of THIS pending step-up |
| subject | `oid` differs from the session's `oid` (or, for an older session, the token's `preferred_username`/`email` differs from the signed-in account) |
| `auth_time` | missing, or older than `MaxAuthAgeSeconds` |
| `acrs` | does not contain the declared context |
| `amr` | absent (unless the record waives it with `RequireAmr = false`), or without a phishing-resistant value (`fido`, `hwk` by default) |

Then the receipt is minted, each target stamped, and the browser returned to `returnUrl` with
`stepUp=done` (or `stepUp=failed&reason=…`).

## The passkey rung (non-Microsoft accounts)

- **Enrolment** from *Settings → Security*: `navigator.credentials.create` against
  `/auth/step-up/passkey/register`; the server verifies the attestation with the maintained
  FIDO2 library (`Fido2NetLib`) and stores `{user}/_Passkey/{credentialId}` — credential id, public
  key, sign counter, AAGUID, created-at. Never a secret. The first passkey may be enrolled only
  within ten minutes of a sign-in; every further one needs a step-up with an existing one, so a
  stolen session cannot add its own authenticator.
- **Assertion** at step-up: the challenge is derived from the pending step-up (user, targets,
  nonce), `userVerification=required`; the server verifies the signature, the origin and RP id,
  the UV flag, and a sign counter that moved forward, then mints the same receipt with
  `method=passkey`.

## The TOTP rung (only where no passkey is possible)

Offered only when the account has **no** passkey AND the browser reports neither a platform
authenticator (`PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable()`) nor
WebAuthn at all. The server enforces the half it can see: an account with a passkey is never
offered, and never accepts, TOTP — so the fallback cannot be used to downgrade.

- Enrolment from *Settings → Security*: a QR code of the `otpauth://` URI; the secret is stored
  encrypted with `IProviderKeyProtector`, never in configuration; ten one-time recovery codes are
  shown once and stored as hashes.
- Verification: RFC 6238 (SHA-1, 30 s, 6 digits, ±1 step), each time step accepted once.

## Consumers

| Approval path | Where the receipt is consumed | Binding |
|---|---|---|
| `Hosting/InstanceAction` *Approve* | `ActionsExecutor.ApprovalGate`, consumed in the control plane just before the run | `ApprovedPlan` (plan digest) |
| `Essentials/OperationRequest` *Approve* | `OperationRequestControlPlane.ApproveFlow`, before the claim | `ScriptHash` |
| `Governance/Activity` signatures | `ActivityGates.SignatureRefusal`, consumed with the signature | the activity `ContentHash` |
| `Hosting/ApprovalInbox` bulk approve | inherits — one step-up covering every selected target | each row's own hash |

`SoleMaintainerApproval` stays what it is — *may this person approve their own request* — and the
receipt is required on top of it, never instead of it.

## Status

| Piece | State |
|---|---|
| receipt, seal, consumption, verdict, node types | core — this design's first change |
| Entra rung, `mw_idp`/`mw_oid`/`mw_tid`/`mw_auth_time` on the session cookie, record keys | core — first change |
| passkey rung | core — second change |
| TOTP rung | core — third change |
| consumers | MeshWeaver.Plugins — after the core contract is in a sealed set |
| public links (`link.publish`) consuming the receipt | Refs #4306, after the consumers |

## Related

- [Authorize as Caller, Execute as System](../AuthorizeAsCallerExecuteAsSystem) — the approval is authorized as the caller; the receipt is part of that authorization.
- [Sole-Maintainer Approval](../SoleMaintainerApproval) — who may approve; this page is about proving it is really them, just now.
- [Access Control](../AccessControl) — node-type access rules.
- [Instance Secrets](../InstanceSecrets) — the master key the seal and the TOTP secret derive from.
- [Policy Not Prose](../PolicyNotProse) — the `approval-step-up` row.
