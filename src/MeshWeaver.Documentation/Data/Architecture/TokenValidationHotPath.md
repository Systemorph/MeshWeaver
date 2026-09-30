---
NodeType: Markdown
Name: "Token Validation Hot Path"
Abstract: "Why an MCP client signed out while its token was valid (memex, 2026-09-29/30), and the three changes that stop it: /mcp answers a validation stall with 503 + Retry-After instead of a 401 auth challenge; the auth middleware validates from the authoritative store (one shared verdict, a short per-replica cache of successes) instead of depending on an ApiToken hub hop being answered; and /ready holds a replica out of rotation only when it genuinely cannot authenticate a token."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#1565c0'/><path d='M8 11V8a4 4 0 0 1 8 0v3M7 11h10v8H7z' fill='none' stroke='white' stroke-width='1.6' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Authentication"
  - "MCP"
  - "Probes"
---

# Token Validation Hot Path

> **Maintainer, 2026-09-30:** *"this must not happen. find out why. we must show as ready only
> when this stuff can be served."*

## What happened

On 2026-09-29 and 30 the maintainer's MCP connection to memex.systemorph.com kept asking to sign in
again, although the token was a 365-day `mw_` token that had not been revoked. Measured on replica
`memex-portal-deployment-855468d589-r5rmp`, which had been Ready for about 40 minutes:

- 05:23:17Z — `UserContextMiddleware` sent `ValidateTokenRequest` to `ApiToken/342abeed8e6d`. The
  delivery trail reads `POSTED → mesh/… → ROUTED onTarget=False state=Forwarded`, and then nothing:
  *"the delivery reached a hub but no handler was ever entered"*. The ApiToken handler always posts
  a verdict within ~20 s, so the request was lost between the router's forward and the ApiToken
  activation.
- 05:24:16Z — the request timed out after 60 s and the middleware answered **503 + Retry-After**
  (the issue #637 path). The same happened ten times until 05:28Z.
- The MCP client then reported *"needs you to sign in again"*.

**Not established:** why the forwarded request was never handled (a stale grain directory entry
after the day's rolls, a wedged activation — MeshWeaver#2896 — or a lost reply). The replica had
been Ready for 40 minutes and no roll was running. It has not recurred on the replicas started after
07:52Z. It is filed to triage as a routing defect of its own; the changes below remove the
dependency on it rather than claiming to have fixed it.

## Why a stall became a sign-out

`/mcp` authorizes with the MCP scheme, which forwards AUTHENTICATION to the ApiToken scheme but
wrote its CHALLENGE itself (`ForwardChallenge = null`). The ApiToken handler flags a request whose
validation could not run, and its own challenge answers 503 — but that override never ran on
`/mcp`. A stall there answered **401 + `WWW-Authenticate: Bearer resource_metadata=…`**, which is
precisely the signal an MCP OAuth client acts on by discarding its token and starting the
authorization flow again. `McpUnavailableValidationIsNotASignOutTest` drives this over real HTTP
with the production wiring and fails without the fix.

## The three changes

1. **A stall is never an auth challenge.** The MCP scheme's `ForwardDefaultSelector` sends the
   challenge of a request flagged *validation unavailable* to the ApiToken scheme — `503 +
   Retry-After`, token kept — and every other challenge stays on the MCP scheme (`401` + discovery).
   A genuinely invalid token still gets the discovery challenge (the test's positive control).
2. **Authentication does not depend on a hub hop being answered.** One verdict,
   `ApiTokenVerdict.Decide` (Mesh.Contract), used by the `ApiToken/{hashPrefix}` hub handler and by
   the HTTP middleware. The middleware tries, in order: a positive verdict this replica reached within
   `ValidatedTokenCache.Ttl` (60 s); the verdict over a read straight from the authoritative store
   (`IStorageAdapter`, which bridges its I/O through `IIoPool`); and only then the hub, exactly as
   before. **Only a SUCCESS short-circuits** — a negative or unavailable direct verdict is re-asked of
   the hub — so the fast path can only add acceptances the hub would also give and can never turn a
   valid token into a 401.
3. **Readiness means "can authenticate a token".** `TokenValidationReadiness` (tag `ready`, so it
   is read by `/ready`) runs a canary every 15 s: the same verdict, over the same store, for a
   token that cannot exist. A definitive "not found" proves the path; UNAVAILABLE is a failed
   sample. The probe reads the last sample and does no I/O, keeping `/ready` light (the 2026-07-21
   death spiral). A replica is **not ready until its first sample**, stays ready (Degraded) through
   one or two failures, and leaves rotation only after **three consecutive failures (~45 s)**. It
   returns on the first good sample.

## The trade-offs, stated

- **Revocation latency.** A token revoked while cached keeps authenticating on that replica for at
  most 60 s. Only successes are cached; negatives and faults never are.
- **Fleet-wide unready.** If the token STORE is down for every replica, every replica goes unready.
  That is correct: none of them can authenticate a call, and browser sessions read the same store.
  One slow read cannot do it — that takes 45 s of consecutive failures.
- **What the canary does not prove.** It proves the store read the hot path uses. It does not prove
  the ApiToken hub path, which is now only a fallback for negatives and faults.

## Where it lives

| Piece | File |
|---|---|
| The shared verdict | `src/MeshWeaver.Mesh.Contract/Security/ApiTokenVerdict.cs` |
| The hub handler | `src/MeshWeaver.Graph/Configuration/ApiTokenNodeType.cs` |
| Middleware fast path + cache | `src/MeshWeaver.Hosting.AspNetCore/Portal/UserContextMiddleware.cs`, `ValidatedTokenCache.cs` |
| Challenge routing | `memex/Memex.Portal.Shared/Authentication/McpAuthenticationExtensions.cs` |
| Readiness canary | `memex/aspire/Memex.Portal.ServiceDefaults/TokenValidationReadiness.cs` |
| Tests | `test/Memex.Portal.Shared.Test/{McpUnavailableValidationIsNotASignOut,TokenValidationHotPath,TokenValidationReadiness}Test.cs` |

Related: [Probe Semantics](../ProbeSemantics), [Stale State Until a Recycle](../StaleStateUntilRecycle).
