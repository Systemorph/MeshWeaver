---
Name: Invitation-Only Onboarding
Description: "Restrict onboarding to invited emails: how the InvitationOnly feature flag works end-to-end, where invitations are stored, the admin Invitations tab, and how to send invitation email via Microsoft Graph (M365)."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-10 5L2 7"/></svg>
Category: Architecture
---

# Invitation-Only Onboarding

By default the Memex portal is **open** — anyone who authenticates may self-provision an account
through `/onboarding` (see [Memex Cloud Deployment → Onboarding](/Doc/Architecture/MemexCloudDeployment)).
**Invitation-only mode** narrows this: an admin invites an email address, the system emails that
person from the M365 mailbox the portal sends and receives as, and only an invited email may complete
onboarding. Every other email is refused at the gate — except on an instance whose own Entra directory
demonstrably gates sign-in, which is exempt (see
[Exempt where the instance's own directory gates sign-in](#exempt-where-the-instances-own-directory-gates-sign-in)).

Turn it on with one flag (see [Feature Flags](/Doc/Architecture/FeatureFlags)):

```bash
Features__Onboarding__InvitationOnly=true
```

> **Acceptance model — verified-email allowlist.** The address onboarding uses is always a
> VERIFIED one, and it is never typed: either the identity provider (Entra, Google, …) delivered it
> as an email claim, or the invitation email's **signed link** proved it (see
> [The invitation link token](#the-invitation-link-token)). A **Pending** invitation matching that
> verified email unlocks onboarding, after which the invitation flips to **Accepted**. The first-user
> bootstrap exception still applies — a brand-new deployment with zero User nodes always lets the
> very first user in, so it can never lock itself out. The one other way in is the
> **own-directory exemption**: where Microsoft is the only sign-in route, the tenant names one
> directory and Microsoft Graph confirms the instance's Entra application requires user assignment,
> the directory's assignment list is the allowlist and no invitation is needed.

---

## End-to-end flow

<svg viewBox="0 0 720 230" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:720px;height:auto;display:block;margin:16px auto;" font-family="sans-serif" font-size="12"><defs><marker id="iarr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/></marker></defs><rect x="20" y="30" width="150" height="56" rx="8" fill="#1e3a5f" stroke="#2563eb" stroke-width="1.5"/><text x="95" y="54" text-anchor="middle" fill="#93c5fd" font-weight="bold">Admin</text><text x="95" y="72" text-anchor="middle" fill="#60a5fa">Invitations tab</text><rect x="20" y="140" width="150" height="56" rx="8" fill="#2d1e3a" stroke="#9333ea" stroke-width="1.5"/><text x="95" y="164" text-anchor="middle" fill="#d8b4fe" font-weight="bold">Invitation node</text><text x="95" y="182" text-anchor="middle" fill="#c084fc">Admin/Invitation/{slug}</text><rect x="285" y="30" width="150" height="56" rx="8" fill="#3a2a1e" stroke="#ea580c" stroke-width="1.5"/><text x="360" y="54" text-anchor="middle" fill="#fdba74" font-weight="bold">Invitation email</text><text x="360" y="72" text-anchor="middle" fill="#fb923c">Microsoft Graph</text><rect x="285" y="140" width="150" height="56" rx="8" fill="#1e3a2f" stroke="#16a34a" stroke-width="1.5"/><text x="360" y="164" text-anchor="middle" fill="#86efac" font-weight="bold">Invitee signs in</text><text x="360" y="182" text-anchor="middle" fill="#4ade80">IdP-verified email</text><rect x="550" y="85" width="150" height="56" rx="8" fill="#1e3a2f" stroke="#16a34a" stroke-width="1.5"/><text x="625" y="109" text-anchor="middle" fill="#86efac" font-weight="bold">Onboarding gate</text><text x="625" y="127" text-anchor="middle" fill="#4ade80">Pending? → Accept</text><line x1="95" y1="86" x2="95" y2="140" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/><line x1="170" y1="52" x2="285" y2="55" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/><line x1="360" y1="86" x2="360" y2="140" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/><line x1="435" y1="168" x2="560" y2="130" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/></svg>

1. An admin opens **Admin app (`/Admin`) → Invitations**, enters an email, clicks **Invite**.
2. `InvitationService.CreateInvitation` writes a `Pending` `Invitation` node. **It does not send the
   email.** `InvitationEmailSender` — a hosted service — watches Pending invitations whose
   `EmailSentAt` is null, sends through `IEmailSender`, and stamps `EmailSentAt` so it never
   re-sends. Decoupling it from the creation entry point means an invitation created from the
   settings tab, from MCP (`create`), or from a REST call is emailed exactly once.
3. The invitee follows the email's link (`/auth/invite?token=…`), signs in via any configured IdP,
   and lands on the invited Space. `OnboardingMiddleware` finds no User node for the email and
   redirects to `/onboarding`, where the address is shown read-only — there is no field to type into.
4. The onboarding gate looks up a Pending invitation for the verified email. If found, the profile
   form is shown; on submit the user is created **and the invitation flips to Accepted**. If not
   found, the page shows **"Invitation Required"** and `CreateUser` is refused — unless the
   own-directory exemption holds for this sign-in (below), in which case the form is shown and the
   user is created without an invitation.

### One node per email, many invitations over its life

The node is keyed by the **email** (it is the onboarding allowlist), so every Space invite to a
not-yet-registered address — `SpaceInviteService.GrantOrScheduleAccess`, the Space invite and the
Access Control "grant by email" alike — **overwrites** it with a new `Invitation`: a fresh `Id`, its
own `SpacePath` and `Note`, `EmailSentAt` cleared. The deferred grants do not share it: each
(email, node) pair has its own `EventSubscription`, so every scheduled grant lands on sign-up.

`InvitationEmailSender` therefore treats the invitation's **`Id`**, not the node path, as the unit it
emails (MeshWeaver#7481). Two rules follow, and breaking either loses an email silently:

- **The claim is a fold on the node's CURRENT content**, through `GetMeshNodeStream(path).Update`,
  and stamps `EmailSentAt` only when the current invitation is the one it was offered. It used to
  write the whole node back from the query snapshot it was offered; the query lags the store, so a
  claim for the first of two back-to-back invites restored the first Space over the second, and the
  second was never announced. That is the CQRS rule — never decide or write content from a query.
- **The in-process de-dup is keyed per invitation (`{path}#{Id}`)**, never per path. Keyed by path,
  the first email for an address suppressed every later invitation to it until the process restarted.

An offer the node has already moved past (a newer invite overwrote it before the claim ran) is still
emailed, unstamped: it was a real invite and its Space is announced nowhere else. Back-to-back invites
to two Spaces therefore produce two emails, one per Space.

### The host's personal message

The Space **Invite** form has an optional **Personal message** field. What the host types travels on
the invitation as `Invitation.Message` (`SpaceInviteService.Invite(…, message)`, or
`GrantOrScheduleAccess(…, note, message)`), trimmed, and blank stores nothing. The invitation email
renders it as its own paragraph, HTML-encoded (MeshWeaver.Plugins#4551).

It is deliberately a separate field from `Note`. The platform fills `Note` with an admin-facing
description ("Invited to space …", "Invited to group … as …"), and that text must never reach the
invitee's inbox. An address that already has an account is granted at once and is sent no
invitation email, so the message only travels with an invitation.

### The language of the invitation email

The invitee has no account yet, so there is no profile to read a language from. **The inviting host
picks it, and it defaults to the host's own language** (#7482). The Space **Invite** form has a
**Language of the invitation email** picker (endonyms: English, Deutsch) that starts at the host's
`AccessContext.Locale`; the pick travels on the invitation as `Invitation.Locale`
(`SpaceInviteService.Invite(…, message, locale)`, or `GrantOrScheduleAccess(…, note, message, locale)`).
A null or blank pick is the host's own language, read on the host's turn before the System-run
lookup and writes; a group invitation (`InviteToGroup`) stamps the host's language the same way. The
value is always a supported tag (`Locales.Resolve`), and it is never read from the process culture.

`InvitationEmail.Build(invitation, spaceName, baseUrl, inviteUrl)` (MeshWeaver.Graph) renders the
subject and the HTML body in that language: every line comes from the catalog
(`notification.email.invitation.*`), the host's personal message is its own paragraph, and the
standing footer of `EmailTemplate` follows the same tag. `inviteUrl` is the signed link the sender
minted (next section); when given, the button points at it unchanged in both the Space and the
deployment-wide variant, and only without it does the button fall back to the Space or the portal. An invitation written before the field
existed carries no language and renders in English. The emailer in MeshWeaver.Plugins
(`InvitationEmailSender.BuildInviteEmail`) is the one that sends it, and it builds the email through
this method (MeshWeaver.Plugins#4706), passing its signed invitation link as `inviteUrl`. A
deployment whose Plugins build predates that change still sends its own English text, whatever the
invitation says.

### The invitation link token

An invited person was once asked to TYPE an email whenever their sign-in provider returned no email
claim, and the typed, never-verified address became the key for the invitation admission and for
every pending Space grant that fires on `User.email` (MeshWeaver.Plugins#4515). The link in the
invitation email now carries a signed token, and the onboarding page has no editable email at all.

- **Minted** by the invitation emailer when it sends: `InvitationLinkToken.Issue` protects the
  invitation's path, its `Id`, the invited address and the Space with ASP.NET Core Data Protection —
  the key ring the portal's sign-in cookies already use, shared by every replica — through a
  time-limited protector. The crypto is the platform's; nothing here signs by hand.
- **Single-purpose**: the protector purpose `Memex.Invitation.OnboardingLink.v1` isolates it from
  every other protected payload, so no other token can be replayed as an invitation.
- **Time-bounded**: valid for 14 days from the send; an expired token fails to unprotect exactly as
  an altered one does. A person who misses it is re-invited, which mints a new invitation and email.
- **Bound to the invitation node**: it binds only while `Admin/Invitation/{slug}` still holds that
  exact invitation (`Id`), Pending, for that address. A re-invite, an acceptance or a revocation
  retires every token minted before it.

Following the link (`InvitationLinkController`, `/auth/invite`):

| Session | Result |
|---|---|
| Not signed in | Sign-in, then back to the link |
| Token altered, expired or foreign | Refused — `/onboarding?inviteRefused=invalid` |
| Verified email claim equal to the token's address | Navigates to the invited Space |
| Verified email claim for a DIFFERENT address | Refused (`wrongAccount`): a token for one address never onboards another |
| No verified address, invitation still current | The session cookie is re-issued carrying the token's address (as a provider with an email claim would have issued it); then the invited Space |
| No verified address, invitation no longer current | Refused (`notPending`) |

The onboarding page reads only the verified address. With none — a provider that returned no email
claim and no invitation link followed — it explains how to get one and shows no form; a self-sign-up
on an open portal therefore needs a provider that delivers an email claim.

---

## Where invitations live

Invitations are MeshNodes of type **`Invitation`** stored in the always-present **Admin** partition
at `Admin/Invitation/{slug}` (the slug is the lowercased email with non-alphanumerics replaced by
`_`). The content record is `Invitation`:
`Email`, `InvitedBy`, `InvitedAt`, `Status` (`Pending`/`Accepted`/`Revoked`), `AcceptedAt`, `Note`.

The onboarding gate must find an invitation **by email, globally, before the user has any identity**.

> 🚨 **Invitation queries MUST be path-scoped — `path:Admin/Invitation`.** `InvitationNodeType`
> registers a `QueryRoutingHints { Partition = "Admin" }` rule for path-less `nodeType:Invitation`
> queries, but **that rule is currently inert**: `PostgreSqlPartitionedMeshQuery` routes purely by
> the path's first segment and does not consume `QueryRoutingHints` yet. A path-less
> `nodeType:Invitation` query therefore goes through the cross-schema fan-out, which **excludes the
> `admin` schema** — so it silently returns **zero rows** and every invited user is refused. The
> rule is kept only so the hint is in place once the router honours it.
>
> `InvitationService`, `InvitationEmailSender` and `InvitationsSettingsTab` all path-scope for this
> reason. Do the same in any new caller:
>
> ```csharp
> // ✅ path-scoped — resolves the admin schema via the first path segment
> "path:Admin/Invitation scope:children nodeType:Invitation"
>
> // ❌ path-less — inert routing hint, cross-schema fan-out skips `admin`, 0 rows
> "nodeType:Invitation"
> ```

> **Why not the auth-mirror trigger?** The V27 auth-mirror trigger only mirrors
> `User/Group/Role/VUser/ApiToken` into the `auth` schema; it would **silently drop** `Invitation`
> rows. Admin-partition storage + the routing rule avoids any schema/migration change. See
> [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).

All invitation writes (create, accept, revoke) target the Admin partition where the caller has no
rights, so `InvitationService`
wraps them in `accessService.ImpersonateAsSystem()` — the same infrastructure-write pattern as
`UserOnboardingService.CreateUser`. See [Access Context Propagation](/Doc/Architecture/AccessContextPropagation).

---

## The onboarding gate

The **security boundary** is the `CreateUser` call in
`Onboarding.razor`, not the UI. The
gate adds an invitation synced query alongside the existing first-user / username / email checks and,
when `InvitationOnly` is on and this is not the first user, refuses unless a Pending invitation
matches the email or the own-directory exemption holds. On success it chains
`InvitationService.MarkAccepted` when an invitation was used. The page renders the form for admitted
visitors and an **"Invitation Required"** message for everyone else (messaging only — the real gate
is at `CreateUser`, which re-reads the same decision).

### Exempt where the instance's own directory gates sign-in

Invitation-only is **exempt** on an instance whose own Entra directory decides who may use it. Three
conditions, all required, all re-checked **at every sign-in**:

1. **Microsoft is the only sign-in route.** Any second route (Google, LinkedIn, Apple, GitHub, the
   developer login) admits identities no directory issued, so it voids the exemption.
2. **`Authentication:Microsoft:TenantId` names one directory** — not unset, `common`,
   `organizations` or `consumers`. The handler then signs in with the authorization-code flow
   against that tenant's authority, so every identity reaching the portal was authenticated by that
   directory.
3. **Microsoft Graph confirms the instance's Entra application requires user assignment**
   (`appRoleAssignmentRequired = true` on its service principal). A named tenant alone proves only
   which directory AUTHENTICATES a visitor; without assignment required, every member of the tenant
   can sign in, and the exemption would open an invitation-only portal to all of them.

**How condition 3 is read.** The portal asks Graph with the instance's OWN credential
(`Authentication:Microsoft:ClientId` / `ClientSecret`, an app-only token from the tenant's token
endpoint): `GET /v1.0/servicePrincipals(appId='{clientId}')?$select=appRoleAssignmentRequired`. The
call runs on the HTTP I/O pool. A successful reading is reused for at most two minutes, so turning
assignment off closes the exemption within that window; a failed reading is never cached.

**It fails closed.** No client secret, a refused token, a 403, a missing service principal or an
unparseable answer each mean *not exempt*: invitation-only applies, and the portal logs a Warning
starting `[InvitationOnly] The own-directory exemption does NOT apply` that names the cause.

🚨 **The Graph read needs a permission the sign-in app does not have by default:** the
`Application.Read.All` **application** permission on the instance's Entra app registration, with
admin consent granted in that tenant. Until the tenant's administrator grants it, Graph answers 403
and the exemption stays off — invitations remain the way in. Granting it is an act of the tenant's
owner, never of the platform.

**Sessions are bound to the sign-in configuration.** The auth cookie lives fourteen days and slides,
so a session opened through a route that has since been turned off — or against a tenant that has
since changed — would otherwise outlive the change. At sign-in the portal stamps a fingerprint of
the sign-in configuration (which routes are on, their client ids, the Microsoft tenant; never a
secret) into the cookie, and on every request it rejects and deletes a cookie whose fingerprint no
longer matches, or that carries none. The visitor signs in again under the current routes. Rotating
a client secret does not change the fingerprint. A cookie issued before the stamp existed carries no
fingerprint and is rejected, so each user signs in once more after the change ships.

The exemption never opens a closed registration (`InvitationOnly` and `AllowSelfOnboarding` both
off) and never promotes anybody: the first-user rule is unchanged.

The decision lives in the portal's onboarding gate (MeshWeaver.Plugins: `OnboardingGate.Decide`, fed
by `EntraAppAssignmentProbe`; the cookie binding is `SignInConfigurationStamp`). The deployment-record
pre-flight checks the configuration shape (conditions 1 and 2); only the running portal can read
condition 3. Decided on Systemorph/Memex#633.

### A request that died while its identity was being resolved

`OnboardingMiddleware` runs in front of every page and API route and decides — from the signed-in
user's `User` node and roles — whether to pass the request on, redirect it to `/onboarding`, or
answer `503` because identity could not be resolved. That wait is **deliberately deaf to
`HttpContext.RequestAborted`**: abandoning it half-way would leave the request with no outcome, and
the lookup carries its own bound. The consequence is that the request the middleware resumes may
already be dead.

It therefore checks `RequestAborted` **after** the decision arrives and, when the connection is gone,
hands nothing to the endpoint. Before that check (#4859) a burst of five requests on the public instance was
passed to a JSON endpoint after Kestrel's shutdown had aborted their connections and disposed its
transport; every response write rented from the dead pool and died on
`ObjectDisposedException: 'MemoryPool'` at `Http1OutputProducer.GetFakeMemory`, attributed to this
middleware because it was the frame that resumed them. (In Kestrel, `GetFakeMemory` is the path a
write takes once its connection has already ended, and the pool it rents from belongs to the
transport — so that stack is a write arriving after shutdown tore the HTTP transport down, whichever
way the connection itself ended.)

🚨 **Not answering a dead request must not erase what the crash was evidence of.** An abort while the
host is stopping is how Kestrel ends a shutdown drain that ran out of budget, so that case is logged at
**Warning** (`the connection … was ABORTED while the host is stopping`); a burst of those lines at one
instant says the host's shutdown budget ran out before HTTP drained, which is a question about the
teardown ahead of Kestrel, not about this middleware. A client that simply left a running portal is
logged at Debug. Pinned by `OnboardingMiddlewareAbortedRequestTest`.

---

## The admin Invitations tab

`InvitationsSettingsTab` adds an **Invitations** tab under the **Administration** settings group,
registered through `AddGlobalSettingsMenuItems` (wired in `MemexConfiguration`). The gate is
`AdminMenuGate.IsPlatformAdmin(host)` — reactive, so the tab appears as soon as the platform-admin
grant surfaces.

🚨 **"Platform admin" means `Permission.All` at scope `Admin`, not root-level.** A root-level grant
is the data-superuser shape and is deliberately not how platform admins are provisioned — see
[Access Control → The Admin partition](/Doc/Architecture/AccessControl). (The XML doc comment on
`InvitationsSettingsTab` still says "root-level `Permission.All`"; the code does not.)

The tab lets an admin enter an email + optional note and **Invite** (which creates the node — the
hosted `InvitationEmailSender` sends the mail), lists all invitations with their status, and
**Revoke**s a Pending one.

---

## Sending email (Microsoft Graph)

The portal had no email infrastructure; this feature adds a small sender that reuses your M365
tenant. It is configured by the **`Email`** section and **disabled by default** — when disabled, a
`NoOpEmailSender` logs the would-be
send and reports success, so local dev and tests never send mail.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Email:Enabled` | bool | `false` | When `false`, the NoOp sender is registered. |
| `Email:MailboxAddress` | string | `""` | The mailbox the portal sends **and** receives **as** — a real/shared mailbox (e.g. `memex@yourtenant.com`). |
| `Email:TenantId` | string | `""` | Entra tenant id (client-secret flow). |
| `Email:ClientId` | string | `""` | App-registration client id (client-secret flow). |
| `Email:ClientSecret` | string | `""` | App-registration client secret (keep in Key Vault). |
| `Email:UseManagedIdentity` | bool | `false` | When `true`, authenticate via `DefaultAzureCredential` (managed identity) instead of a client secret. |

`GraphEmailSender` calls Graph `/users/{mailbox}/sendMail` and returns `IObservable<bool>`. 🚨 It
bridges the async Graph call through a **bounded HTTP `IIoPool`** (`_http.Run(...)`), **not**
`Observable.FromAsync` — a bare `FromAsync` runs the prologue on the subscribing thread and
deadlocks under a blocking subscriber. `Observable.FromAsync` is forbidden everywhere in `src/`
outside `IoPool` itself; there is no "not hub-reachable, so it's fine" exemption. See
[Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

### Azure setup (one-time)

`/sendMail` with client credentials uses the **`Mail.Send` application permission**, which is
distinct from the delegated sign-in app. Recommended:

1. Register a dedicated Entra app (or reuse the managed identity in production).
2. Add the **`Mail.Send` application permission** and **grant tenant-admin consent** — without it
   Graph returns **403**.
3. Provision a real licensed or shared **mailbox** the portal sends and receives as, and that the
   app is allowed to act as (application access policies may scope this).
4. **Production:** prefer `Email:UseManagedIdentity=true` and grant the managed identity the
   `Mail.Send` app role; keep no secret in config. **Self-host:** supply `TenantId`/`ClientId`/
   `ClientSecret` (the secret belongs in Key Vault — see
   [Memex Cloud Deployment → Secrets](/Doc/Architecture/MemexCloudDeployment)).

```json
{
  "Email": {
    "Enabled": true,
    "MailboxAddress": "memex@yourtenant.com",
    "TenantId": "<tenant-guid>",
    "ClientId": "<app-client-id>",
    "ClientSecret": "<from-key-vault>",
    "UseManagedIdentity": false
  }
}
```

Registration lives in `MemexConfiguration.ConfigureMemexServices`: `IEmailSender` resolves to
`GraphEmailSender` when `Email:Enabled=true`, else `NoOpEmailSender`.

---

## Enabling the feature

```bash
# Require invitations…
Features__Onboarding__InvitationOnly=true
# …and turn on email so invitations actually go out.
Email__Enabled=true
Email__MailboxAddress=memex@yourtenant.com
Email__TenantId=<tenant-guid>
Email__ClientId=<app-client-id>
Email__ClientSecret=<from-key-vault>
```

With `Email:Enabled=false` you can still run invitation-only mode — invitations are created and
enforced, but no email goes out (the admin shares the portal link out-of-band).

---

## Testing & verification

- **Unit** — `InvitationServiceTests`
  (real mesh, no mocks): `CreateInvitation` writes a queryable Admin-partition node;
  `FindPendingInvitation` returns it (null when absent); `Revoke`/`MarkAccepted` flip status; the
  NoOp sender returns success without sending.
- **Manual** — as a platform admin open **Admin app (`/Admin`) → Invitations**, invite an
  email, then sign in as a non-invited email (→ "Invitation Required") and as the invited email
  (→ profile form → completes → invitation shows **Accepted**).

---

## Related

- [Feature Flags](/Doc/Architecture/FeatureFlags) — the `Features` section reference (the `InvitationOnly` flag and onboarding modes).
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — partitions, schemas, and the auth-mirror trigger.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — why invitation writes impersonate as System.
- [Synced Mesh-Node Queries](/Doc/Architecture/SyncedMeshNodeQueries) — `workspace.GetQuery`, used by the gate and the tab.
