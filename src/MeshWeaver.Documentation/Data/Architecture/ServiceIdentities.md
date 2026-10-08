---
Name: Service Identities
Category: Architecture
Description: Non-person principals — an integration, a bot, a CI job — that authenticate with their own mw_ tokens, are granted access like any subject, are audited under their own id, and never hold platform administration. Where the records live, how tokens are issued, rotated and revoked, and the five places the platform keeps a service from becoming a person or an admin.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><circle cx="12" cy="5" r="2"/><path d="M12 7v4"/><line x1="8" y1="16" x2="8" y2="16"/><line x1="16" y1="16" x2="16" y2="16"/></svg>
---

# Service Identities

Every `mw_` API token used to be issued for the **signed-in person** and nobody else: the personal
token tab, `POST /api/tokens`, the OAuth `/token` exchange and the MCP back-connection all mint for
`AccessService.Context`. A caller that is not a person — a name-check endpoint, a CI job, a partner
integration — therefore had to borrow a person's token: its writes were audited as that person and it
carried that person's whole reach. A **service identity** is the caller's own principal.

## The model

| | |
|---|---|
| **Record** | `Admin/_ServiceIdentity/{objectId}`, node type `ServiceIdentity` (`ServiceIdentity` content: description, issued by/at, revoked flag). |
| **Object id** | `svc-{name}` — the node id, and the `AccessContext.ObjectId` its tokens authenticate as. `ServiceIdentity.ObjectIdFor("Name Check")` = `svc-name-check`. |
| **Tokens** | ordinary `ApiToken` rows at `Admin/_ServiceIdentity/{objectId}/ApiToken/{hashPrefix}`, indexed at `ApiToken/{hashPrefix}` like every token, carrying `ServiceIdentityPath`. Shown once, stored hashed, optional expiry, `LastUsedAt` stamped. |
| **Grants** | an ordinary `AccessAssignment` at `{scope}/_Access/{objectId}_Access` with `AccessObject = objectId`. The permission evaluator treats it like any subject. |
| **Audit** | `CreatedBy` / `LastModifiedBy` carry the service id — never the admin who issued the token. |

The records live in the **Admin partition** for the reason `BuildPrincipal` does: only a global admin
can write there, so only a global admin creates an identity, issues or revokes its tokens, or revokes
the identity — and the subject can never write its own record.

## Managing them — the Admin app

**Admin app → People & sign-in → Service identities** (platform admins only; the area re-checks the
gate for direct links). Create an identity, issue a token (shown once), rotate a token (a new token
with the same label and term length is minted **first**, then the old one is revoked), revoke a token,
grant access at a path with a role, revoke the identity.

A grant **sets** the service's role at that scope — re-granting a different role replaces the
previous one (one role per scope for a machine principal; compose several on the node's Access
Control tab). A grant is written **as the admin**. A global admin is not a data superuser, so the grant succeeds
exactly where that admin may grant at the target scope — the tab does not widen anyone's reach. A
scope owner who is not a platform admin grants a service the same way they grant anyone: an
`AccessAssignment` whose subject is the `svc-…` id.

The verbs behind the tab are `ServiceIdentities.Create / Revoke / Grant / Rotate` and
`ApiTokenService.CreateServiceToken / GetTokensForService / RevokeToken` (memex), which the tests drive
directly.

**What `RevokeToken` answers (MeshWeaver#6026).** It is ONE write — `GetMeshNodeStream(path).Update`,
checked at the token's owning hub against the caller's own permissions and durable when it emits —
and it answers three ways: `true` (the node now carries `isRevoked`), `false` (the path holds no node,
so nothing there authenticates), or a **fault** (the mesh refused, or the node is not a readable
token). A refusal is never folded into `false`: it is a statement about a credential that still
works, so a rotation whose revoke is refused fails rather than reporting success with the old token
live. It used to follow the update with a second, whole-node `SaveMeshNodeRequest` posted from the
root mesh hub — a router-sent write whose outcome had nowhere to go; the post-commit flush made it
redundant and it is gone. Pinned by `TokenRevocationIsIssuedOffTheRouterTest`.

## Validation — both paths

A token carrying `ServiceIdentityPath` authenticates only while its record **exists and is not
revoked**, read from the same authoritative store as the token on **every use**. Revoking the identity
therefore stops every token it holds on the next request, on every replica, without touching the
tokens. Both validation paths apply the same predicate (`ServiceIdentity.Refuse`):

- `ApiTokenService.Validate` (the `ApiToken` authentication handler — storage-direct): a read fault is
  `Unavailable` (503, retryable), never `Invalid` and never `Valid`.
- `ApiTokenNodeType.HandleValidateToken` (the request middleware's hub path): answers
  `ValidateTokenResponse.IsService = true`, which becomes `AccessContext.IsService`.

Both read the record from the authoritative store: a record that was **deleted** rather than revoked
reads as absent and refuses the token definitively (401), exactly like a revoked one; only a store
that cannot answer yields the retryable 503.

A token that names a `svc-…` id **without** an identity path was not minted by the service surface and
is refused on both paths.

## Five guards that keep a service a service

1. **Issuing for someone else names only a service.** `CreateServiceToken` refuses a non-`svc-` id,
   an absent record and a revoked identity before writing anything; `CreateToken` (every person
   surface) refuses a `svc-` id. There is still no way to mint a token for another *person*.
2. **No person can become a service.** Onboarding refuses a username starting `svc-`; the request
   middleware refuses — as anonymous — any session that resolves to a `svc-` object id without
   having been authenticated by that service's token (an e-mail whose local part reads `svc-…`, a
   dev login). The principal-kind claim is honoured only on an identity of the `ApiToken` scheme, so a
   cookie or an external provider cannot assert it.
3. **No service is treated as a person.** No onboarding redirect, no login record in a user
   partition, no logon actions.
4. **A service never holds global admin.** `hub.IsGlobalAdmin(userId)` answers `false` for a `svc-`
   id whatever the grants say; `ServicePrincipalAdminGuard` refuses an `AccessAssignment` in the Admin
   partition whose subject is a service.
5. **A service writes nothing in the Admin partition** — not its own record, not a token, not a
   grant — even if a group membership gave it rights there.

## What it does not do

- A service inherits the `Public` baseline every authenticated caller gets, like any signed-in
  principal. Grant nothing to `Public` you would not grant a service.
- The access-control subject picker lists Users and Groups; a service is granted from the Service
  identities tab or by writing its `svc-…` id as the `AccessObject`.
- Per-caller rate limiting is not part of this change.
