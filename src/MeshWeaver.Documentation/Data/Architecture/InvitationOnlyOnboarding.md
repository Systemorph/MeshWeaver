---
Name: Invitation-Only Onboarding
Description: "Restrict onboarding to invited emails: how the InvitationOnly feature flag works end-to-end, where invitations are stored, the admin Invitations tab, and how to send no-reply email via Microsoft Graph (M365)."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-10 5L2 7"/></svg>
Category: Architecture
---

# Invitation-Only Onboarding

By default the Memex portal is **open** — anyone who authenticates may self-provision an account
through `/onboarding` (see [Memex Cloud Deployment → Onboarding](MemexCloudDeployment.md)).
**Invitation-only mode** narrows this: an admin invites an email address, the system emails that
person from a no-reply M365 mailbox, and only an invited email may complete onboarding. Every other
email is refused at the gate.

Turn it on with one flag (see [Feature Flags](FeatureFlags.md)):

```bash
Features__Onboarding__InvitationOnly=true
```

> **Acceptance model — verified-email allowlist.** There is no token link. The identity provider
> (Entra, Google, …) proves the user owns the email; a **Pending** invitation matching that verified
> email unlocks onboarding, after which the invitation flips to **Accepted**. The first-user
> bootstrap exception still applies — a brand-new deployment with zero User nodes always lets the
> very first user in, so it can never lock itself out.

---

## End-to-end flow

<svg viewBox="0 0 720 230" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:720px;height:auto;display:block;margin:16px auto;" font-family="sans-serif" font-size="12"><defs><marker id="iarr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/></marker></defs><rect x="20" y="30" width="150" height="56" rx="8" fill="#1e3a5f" stroke="#2563eb" stroke-width="1.5"/><text x="95" y="54" text-anchor="middle" fill="#93c5fd" font-weight="bold">Admin</text><text x="95" y="72" text-anchor="middle" fill="#60a5fa">Invitations tab</text><rect x="20" y="140" width="150" height="56" rx="8" fill="#2d1e3a" stroke="#9333ea" stroke-width="1.5"/><text x="95" y="164" text-anchor="middle" fill="#d8b4fe" font-weight="bold">Invitation node</text><text x="95" y="182" text-anchor="middle" fill="#c084fc">Admin/Invitation/{slug}</text><rect x="285" y="30" width="150" height="56" rx="8" fill="#3a2a1e" stroke="#ea580c" stroke-width="1.5"/><text x="360" y="54" text-anchor="middle" fill="#fdba74" font-weight="bold">No-reply email</text><text x="360" y="72" text-anchor="middle" fill="#fb923c">Microsoft Graph</text><rect x="285" y="140" width="150" height="56" rx="8" fill="#1e3a2f" stroke="#16a34a" stroke-width="1.5"/><text x="360" y="164" text-anchor="middle" fill="#86efac" font-weight="bold">Invitee signs in</text><text x="360" y="182" text-anchor="middle" fill="#4ade80">IdP-verified email</text><rect x="550" y="85" width="150" height="56" rx="8" fill="#1e3a2f" stroke="#16a34a" stroke-width="1.5"/><text x="625" y="109" text-anchor="middle" fill="#86efac" font-weight="bold">Onboarding gate</text><text x="625" y="127" text-anchor="middle" fill="#4ade80">Pending? → Accept</text><line x1="95" y1="86" x2="95" y2="140" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/><line x1="170" y1="52" x2="285" y2="55" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/><line x1="360" y1="86" x2="360" y2="140" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/><line x1="435" y1="168" x2="560" y2="130" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#iarr)"/></svg>

1. An admin opens **Settings → Administration → Invitations**, enters an email, clicks **Invite**.
2. `InvitationService.CreateInvitation` writes a `Pending` `Invitation` node and `IEmailSender`
   sends the no-reply invitation email.
3. The invitee signs in via any configured IdP. `OnboardingMiddleware` finds no User node for the
   email and redirects to `/onboarding`.
4. The onboarding gate looks up a Pending invitation for the verified email. If found, the profile
   form is shown; on submit the user is created **and the invitation flips to Accepted**. If not
   found, the page shows **"Invitation Required"** and `CreateUser` is refused.

---

## Where invitations live

Invitations are MeshNodes of type **`Invitation`** stored in the always-present **Admin** partition
at `Admin/Invitation/{slug}` (the slug is the lowercased email with non-alphanumerics replaced by
`_`). The content record is [`Invitation`](../../../../src/MeshWeaver.Mesh.Contract/Invitation.cs):
`Email`, `InvitedBy`, `InvitedAt`, `Status` (`Pending`/`Accepted`/`Revoked`), `AcceptedAt`, `Note`.

The onboarding gate must find an invitation **by email, globally, before the user has any identity**.
That works because [`InvitationNodeType`](../../../../src/MeshWeaver.Graph/Configuration/InvitationNodeType.cs)
registers a query-routing rule sending the path-less lookup to the Admin partition — the exact
proven pattern `User → Auth` and `Role → Admin` use:

```csharp
builder.AddQueryRoutingRule(query =>
    query.ExtractNodeType() == NodeType && string.IsNullOrEmpty(query.Path)
        ? new QueryRoutingHints { Partition = "Admin" }
        : null);
```

> **Why not the auth-mirror trigger?** The V27 auth-mirror trigger only mirrors
> `User/Group/Role/VUser/ApiToken` into the `auth` schema; it would **silently drop** `Invitation`
> rows. Admin-partition storage + the routing rule avoids any schema/migration change. See
> [Postgres Schema Architecture](PostgresSchemaArchitecture.md).

All invitation writes (create, accept, revoke) target the Admin partition where the caller has no
rights, so [`InvitationService`](../../../../memex/Memex.Portal.Shared/Authentication/InvitationService.cs)
wraps them in `accessService.ImpersonateAsSystem()` — the same infrastructure-write pattern as
`UserOnboardingService.CreateUser`. See [Access Context Propagation](AccessContextPropagation.md).

---

## The onboarding gate

The **security boundary** is the `CreateUser` call in
[`Onboarding.razor`](../../../../memex/Memex.Portal.Shared/Pages/Onboarding.razor), not the UI. The
gate adds an invitation synced query alongside the existing first-user / username / email checks and,
when `InvitationOnly` is on and this is not the first user, refuses unless a Pending invitation
matches the email. On success it chains `InvitationService.MarkAccepted`. The page renders the form
for invited users and an **"Invitation Required"** message for everyone else (messaging only — the
real gate is at `CreateUser`).

---

## The admin Invitations tab

[`InvitationsSettingsTab`](../../../../memex/Memex.Portal.Shared/Settings/InvitationsSettingsTab.cs)
adds an **Invitations** tab under the **Administration** settings group. It is gated exactly like the
existing **Global Administration** tab: the provider yields it only when the viewer is the node owner
**and** holds root-level `Permission.All`. Registered via `ConfigureDefaultNodeHub`, that gate means
it surfaces only on a platform admin's own User Settings page — **not** on every node.

The tab lets an admin enter an email + optional note and **Invite** (creates the node and sends the
email), lists all invitations with their status, and **Revoke**s a Pending one.

---

## Sending email (Microsoft Graph)

The portal had no email infrastructure; this feature adds a small sender that reuses your M365
tenant. It is configured by the **`Email`** section and **disabled by default** — when disabled, a
[`NoOpEmailSender`](../../../../memex/Memex.Portal.Shared/Email/NoOpEmailSender.cs) logs the would-be
send and reports success, so local dev and tests never send mail.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Email:Enabled` | bool | `false` | When `false`, the NoOp sender is registered. |
| `Email:NoReplyAddress` | string | `""` | The mailbox to send **as** (e.g. `no-reply@yourtenant.com`). |
| `Email:TenantId` | string | `""` | Entra tenant id (client-secret flow). |
| `Email:ClientId` | string | `""` | App-registration client id (client-secret flow). |
| `Email:ClientSecret` | string | `""` | App-registration client secret (keep in Key Vault). |
| `Email:UseManagedIdentity` | bool | `false` | When `true`, authenticate via `DefaultAzureCredential` (managed identity) instead of a client secret. |

[`GraphEmailSender`](../../../../memex/Memex.Portal.Shared/Email/GraphEmailSender.cs) calls Graph
`/users/{noReply}/sendMail`. It bridges the async Graph call to the codebase's reactive convention
via `Observable.FromAsync` — the sender is not hub-reachable, so this is the sanctioned boundary
(same shape as other HttpClient outbound integrations).

### Azure setup (one-time)

`/sendMail` with client credentials uses the **`Mail.Send` application permission**, which is
distinct from the delegated sign-in app. Recommended:

1. Register a dedicated Entra app (or reuse the managed identity in production).
2. Add the **`Mail.Send` application permission** and **grant tenant-admin consent** — without it
   Graph returns **403**.
3. Provision a real licensed or shared **no-reply mailbox** the app is allowed to send as
   (application access policies may scope this).
4. **Production:** prefer `Email:UseManagedIdentity=true` and grant the managed identity the
   `Mail.Send` app role; keep no secret in config. **Self-host:** supply `TenantId`/`ClientId`/
   `ClientSecret` (the secret belongs in Key Vault — see
   [Memex Cloud Deployment → Secrets](MemexCloudDeployment.md)).

```json
{
  "Email": {
    "Enabled": true,
    "NoReplyAddress": "no-reply@yourtenant.com",
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
Email__NoReplyAddress=no-reply@yourtenant.com
Email__TenantId=<tenant-guid>
Email__ClientId=<app-client-id>
Email__ClientSecret=<from-key-vault>
```

With `Email:Enabled=false` you can still run invitation-only mode — invitations are created and
enforced, but no email goes out (the admin shares the portal link out-of-band).

---

## Testing & verification

- **Unit** — [`InvitationServiceTests`](../../../../test/MeshWeaver.Auth.Test/InvitationServiceTests.cs)
  (real mesh, no mocks): `CreateInvitation` writes a queryable Admin-partition node;
  `FindPendingInvitation` returns it (null when absent); `Revoke`/`MarkAccepted` flip status; the
  NoOp sender returns success without sending.
- **Manual** — as a platform admin open **Settings → Administration → Invitations**, invite an
  email, then sign in as a non-invited email (→ "Invitation Required") and as the invited email
  (→ profile form → completes → invitation shows **Accepted**).

---

## Related

- [Feature Flags](FeatureFlags.md) — the `Features` section reference (the `InvitationOnly` flag and onboarding modes).
- [Postgres Schema Architecture](PostgresSchemaArchitecture.md) — partitions, schemas, and the auth-mirror trigger.
- [Access Context Propagation](AccessContextPropagation.md) — why invitation writes impersonate as System.
- [Synced Mesh-Node Queries](SyncedMeshNodeQueries.md) — `workspace.GetQuery`, used by the gate and the tab.
