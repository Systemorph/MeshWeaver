---
NodeType: Markdown
Name: "MCP Authentication"
Abstract: "How MCP clients authenticate against the MeshWeaver MCP endpoint: personal API tokens, the OAuth flow OAuth-capable clients run instead, what each flow stores and for how long, why a multi-replica portal needs no affinity, and how to read a rejected token's log line."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#f57f17'/><circle cx='9' cy='12' r='3.5' fill='none' stroke='white' stroke-width='2'/><path d='M12.5 12h7M17 12v3M19.5 12v2' stroke='white' stroke-width='2' stroke-linecap='round'/></svg>"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "MCP"
  - "Authentication"
  - "Security"
  - "API Tokens"
---

The MeshWeaver MCP endpoint (`/api/mcp`, with `/mcp` as a permanent compatibility alias — both serve the identical endpoint) uses bearer token authentication. Every MCP client — Claude Code, Cursor, or a custom integration — must include a valid API token in its `Authorization` header. Without one, requests receive `401 Unauthorized` and never reach the MCP handler.

Tokens are personal. Each token is tied to a specific user, so all MCP operations run under that user's identity with full row-level security (RLS) enforced.

<svg viewBox="0 0 760 160" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="50" width="155" height="60" rx="10" fill="#1e88e5"/>
  <text x="77" y="77" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">1 — Generate Token</text>
  <text x="77" y="95" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Settings › API Tokens</text>
  <line x1="155" y1="80" x2="193" y2="80" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <rect x="193" y="50" width="155" height="60" rx="10" fill="#5c6bc0"/>
  <text x="270" y="77" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">2 — Configure Client</text>
  <text x="270" y="95" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Authorization: Bearer mw_…</text>
  <line x1="348" y1="80" x2="386" y2="80" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <rect x="386" y="50" width="155" height="60" rx="10" fill="#43a047"/>
  <text x="463" y="77" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">3 — Authenticate</text>
  <text x="463" y="95" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">SHA-256 hash → ApiToken node</text>
  <line x1="541" y1="80" x2="579" y2="80" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <rect x="579" y="50" width="155" height="60" rx="10" fill="#f57c00"/>
  <text x="656" y="77" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">4 — Access MeshWeaver</text>
  <text x="656" y="95" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">RLS enforced, user identity set</text>
  <text x="77" y="140" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".45" text-anchor="middle">Portal UI / REST API</text>
  <text x="270" y="140" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".45" text-anchor="middle">Claude Code / Cursor / curl</text>
  <text x="463" y="140" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".45" text-anchor="middle">ApiTokenAuthenticationHandler</text>
  <text x="656" y="140" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".45" text-anchor="middle">MCP tools + row-level security</text>
  <text x="380" y="22" font-family="sans-serif" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".7" text-anchor="middle">Bearer Token Authentication Flow</text>
</svg>

*Four steps from token generation to authenticated MCP access.*

---

## Generating a Token

### Via the Web UI

1. Log in to the Memex portal.
2. Navigate to **Settings** (gear icon) on any node.
3. Under the **Security** group, open **API Tokens**.
4. Enter a label (e.g. `Claude Code`) and optionally set an expiry in days.
5. Click **Generate Token**.
6. **Copy the token immediately** — it is shown only once and cannot be retrieved later.

Tokens start with `mw_` followed by a base64url-encoded random string:

```
mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg
```

### Via the REST API

If you prefer automation, the token API accepts a cookie-authenticated session:

```bash
# Create a token
curl -X POST https://your-portal.com/api/tokens \
  -H "Content-Type: application/json" \
  -b cookies.txt \
  -d '{"label": "Claude Code", "expiresInDays": 90}'
```

The response returns the raw token — copy it now, as it is not stored in plaintext:

```json
{
  "rawToken": "mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg",
  "nodePath": "ApiToken/a1b2c3d4e5f6",
  "label": "Claude Code",
  "createdAt": "2025-06-15T10:00:00Z",
  "expiresAt": "2025-09-13T10:00:00Z"
}
```

---

## Configuring MCP Clients

### Claude Code

Add the MCP server to your Claude Code configuration (`.claude/settings.json` or a project-level `claude_code_config.json`):

```json
{
  "mcpServers": {
    "meshweaver": {
      "type": "sse",
      "url": "https://your-portal.com/api/mcp",
      "headers": {
        "Authorization": "Bearer mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg"
      }
    }
  }
}
```

After saving, Claude Code can use MeshWeaver tools (Get, Search, Create, Update, Delete) with your identity and permissions.

### Other MCP Clients

Any MCP client that supports HTTP/SSE transport can connect. The only requirement is sending the `Authorization` header on every request:

```
Authorization: Bearer mw_<your-token>
```

#### Smoke-test with curl

```bash
curl -X POST https://your-portal.com/api/mcp \
  -H "Authorization: Bearer mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"initialize","params":{},"id":1}'
```

A successful JSON-RPC response confirms authentication is working. A `401` means the token is missing, invalid, expired, or revoked — and a `503` with `Retry-After` means the token could **not be checked** right now and must be kept (see *Reading a rejection* below).

---

## OAuth for MCP clients

A client that speaks OAuth — claude.ai connectors, Claude Desktop, Claude Code's `claude mcp add --transport http` — never sees the token page. It discovers the portal as an authorization server and ends up holding the same `mw_` token a manual flow would have produced. The whole flow is served by `OAuthConnectController` in `Memex.Portal.Shared`:

| Step | Endpoint | What happens |
|---|---|---|
| Discovery | `GET /.well-known/oauth-protected-resource`, `GET /.well-known/oauth-authorization-server` | The `401` challenge on `/mcp` names the first; it names the second. Only `authorization_code` with PKCE `S256` is advertised — there is no `refresh_token` grant. |
| Registration | `POST /register` (RFC 7591) | The client sends its name and redirect URIs and receives a `client_id`. **Nothing is persisted.** The id is *derived* — a hash of this portal's origin, the client name and the sorted redirect URIs — so a client that registers again receives the same id and is recognised as the same application. |
| Authorization | `GET /authorize` | The user must hold a portal session. Without one the request is sent to `/login` with a **local** return target (`/authorize?…`) and comes back here after sign-in; with one, an authorization code is minted and the browser is redirected to the client's `redirect_uri`. No consent screen is shown: a signed-in user is authorized for their own identity. |
| Exchange | `POST /token` | The client presents the code, its `client_id`, the same `redirect_uri` and the PKCE verifier. The code is consumed (single use, first delete wins), an `mw_` token is minted for the code's user, and — once the client holds more than its bound of live tokens for the same `client_id` (default 5) — the oldest are removed. |
| Use | `POST /mcp` with `Authorization: Bearer mw_…` | Exactly the manual-token path from here on. |

### What is stored, where, for how long, and who can read it

| Record | Path | Contents | Lifetime | Readable by |
|---|---|---|---|---|
| Authorization code | `Admin/OAuthCode/{hashPrefix}` | The SHA-256 hash of the code (never the code), the user's id/name/email, `client_id`, `redirect_uri`, the PKCE challenge, the creation time | 5 minutes, and deleted the moment it is exchanged; expired rows are swept on the next `/authorize` | System-managed rows in the `Admin` partition — a platform admin's grant, nobody else's (see [Access Control → The Admin partition](/Doc/Architecture/AccessControl)). Excluded from search, create menus and autocomplete. |
| Access token | `{userId}/ApiToken/{hashPrefix}` | The SHA-256 hash of the token (never the token), owner identity, the label `OAuth: {client_id}`, created/expires/last-used stamps, a diagnostic copy of the roles at mint time | 1 year, or until revoked, deleted, or evicted by the per-client bound | The owner, in their own partition — the same row the API Tokens settings tab lists |
| Token index | `ApiToken/{hashPrefix}` | The hash and the path of the token row | Follows its token row: written with it, deleted with it (revocation, expiry sweep, supersede) | Written and deleted under the System identity; the `ApiToken` partition is separately gated and ordinary users hold no grant on it |
| Client registration | — | nothing | — | — |

Two things are deliberately **not** stored: the raw code and the raw token exist only in the client's hands, and no consent or registration record is kept, so there is nothing for a stable `client_id` to be stable *for* on the server — its value is that the client keys its own token cache on it.

### Nothing is per-process — a multi-replica portal needs no affinity

Every record above is written to and read from the shared store directly, on every replica: `/authorize` on one pod and `/token` on the other exchange the same code, and a token minted by one pod validates on the other on the first request. The MCP transport itself is stateless (no per-pod session table), and the cookie-protection keys are shared across replicas, so a two-replica deployment needs no sticky routing for `/token`, `/register` or `/mcp`. The regression controls for both halves are two-instance tests over one store: `OAuthCodeStoreTest.TwoStoreInstances_GenerateOnOne_ExchangeOnOther` and `TokenMintedOnOneReplicaValidatesOnAnotherTest`.

### A bounded number of live credentials per client — and why the bound is not one

A re-authorization **adds** a credential, and the client keeps at most **N live credentials** for the same label `OAuth: {client_id}` — the N newest, the one just minted included. After minting, `/token` evicts every older token beyond that bound. N is configuration: `Mcp:OAuth:MaxLiveCredentialsPerClient` (environment form `Mcp__OAuth__MaxLiveCredentialsPerClient`), **default 5**; a value below 1 is treated as 1. The bound is policy [`oauth-bounded-live-credentials`](/Doc/Architecture/PolicyNotProse).

**Why bounded at all.** Before any bound, every authorization minted a fresh year-long token and left the previous one live, so a reinstall, a new device or a reconnected integration each left another usable key behind — the state the rule was introduced to end was 86 live token rows on one portal.

**Why not one.** The key is the `client_id`, and the `client_id` is derived from the client's name and redirect URIs, so which processes share a credential slot is decided by the **client**:

- A client that binds a fresh callback port per registration gets a fresh `client_id` per registration — two such installations never share a slot, and the bound never fires for them.
- A client that presents the same `client_id` from more than one process shares ONE slot set across those processes. Claude Code does exactly this: it keeps one registration per server per machine in its credential store, reuses its callback port when it is free, and every Claude Code process on that machine reads the same entry. Nothing the exchange receives distinguishes two such processes — `client_id`, `redirect_uri` and `client_name` are identical by construction, and the code and verifier are per attempt — so the portal cannot tell them apart. Under a bound of one, each new sign-in deleted the token every sibling session was using: their next call answered `401` and they had to re-authorize too, which in turn evicted the first one ([#5074](https://github.com/Systemorph/MeshWeaver/issues/5074)).

With a bound of N, up to N concurrent sessions of one installation coexist. Above N the oldest session's token is evicted and that session re-authorizes — with a live portal session silently (`/authorize` redirects straight back with a code), with a lapsed one through a sign-in.

**The rule is convergent under concurrent exchanges.** Each exchange ranks only tokens strictly **older** than its own, in a total order over `(CreatedAt, path)`, keeps the newest N−1 of those that are live, and evicts the other older ones; a newer live token is never touched. A revoked or expired row never holds a kept slot and is always removed, whatever its age. A token among the N newest therefore has at most N−2 tokens between it and any newer exchange, so no exchange ever evicts it, and a listing that trails the store only makes an exchange evict less. The rule is pure (`OAuthCredentialEviction.Evict`) and pinned by `OAuthCredentialEvictionTest`; the end-to-end control is `OAuthBoundedLiveCredentialsTest`.

### Reading a rejection

Every rejected bearer token produces a warning on the portal naming the token's **hash prefix** (the first 12 characters of the hash — also the last path segment of its rows) and the failing **stage**. The stage is the whole diagnosis:

| Stage | Meaning | HTTP answer |
|---|---|---|
| `index-not-found`, `token-not-found` | The store answered that **no row** exists at the path. The token was never minted here, or its row was deleted (revocation from the settings tab, the expiry sweep, or eviction by the per-client bound). | `401` |
| `index-unreadable`, `token-unreadable` | The row **exists** but its content is not a readable token record; the line names the row's version and the content's runtime type. The token was **not** deleted — this replica could not type the row. | `401` |
| `index-hash-mismatch`, `token-hash-mismatch` | A row exists at the prefix but carries a different hash. | `401` |
| `revoked`, `expired` | The row was read and says so. | `401` |
| `index-read-timeout`, `token-read-timeout` | The store could **not** be read within the validation window. No verdict was reached. | `503` + `Retry-After` — the body says the token was **not** rejected and must be kept |

An eviction is logged at Information three times, and the lines say different things: `OAuth: superseding N previous token(s) for user U, client label L — candidates {paths}, keeping {kept path}; a client keeps at most {N} live credential(s)…` is the **intent**, written before any delete has run; `OAuth: evicted credential {path} for user U, client label L — older than the {N} newest live credential(s) of this client…` is written once per token a delete actually removed; `OAuth: superseded R of N previous token(s) … — removed {paths}; kept {kept path}` is the **outcome**, written after every delete has answered (a path a concurrent exchange had already removed, or one whose delete was refused, is not in `removed`). A removed path ends in the same hash prefix the superseded holder's `-not-found` line will name, so one search for the prefix ties the removal to the `401` it caused — read the outcome line for that, never the intent.

**An exchange the client abandoned mints nothing it keeps.** `/token` runs as one reactive chain that the request's abort token can stop WAITING on but not stop RUNNING, so a client that gives up mid-exchange used to leave behind a year-long token nobody received — and, if the abort landed after the mint, also supersede the credential the client still held. The exchange now checks the abort token twice: before the mint (`OAuth /token for client {ClientId}: the client abandoned the exchange before a token was minted — no token issued`) and between the mint and the supersede, where the undelivered token is revoked and the supersede is skipped (`… abandoned the exchange while the token was being minted — revoked the undelivered token {Path}; the client's previous credential was NOT superseded`). A `could not revoke the undelivered token {Path}` warning names the one row that stays live with no holder.

To read these on a deployed portal, use the control instance's `Logs` action rather than the cluster (see [Operating from the Portal](/Doc/Architecture/OperatingFromThePortal)): a `Hosting/InstanceAction` node with `requestedAction: "Logs"`, `deployment` naming the instance, `query: "API token validation"` (or `"OAuth: superseding"`, or the hash prefix itself) and a `sinceMinutes` that covers the moment — the entries land under `Ops/Logs`. The window the instrument answers for is bounded, so a reading is taken while the event is recent, not reconstructed afterwards.

### Verifying the flow on a deployed portal — a positive read

A silent log is not evidence of a working flow; the read must be a line that only a *completed* flow produces. After a roll, have an MCP client whose portal session has lapsed re-authorize, then query `OAuth /authorize` and `Issued OAuth` over the minutes around it. The sequence that proves the login hop returns to the flow is, for one `client_id`: `OAuth /authorize: … authenticated=False` → `redirecting unauthenticated caller to /login?returnUrl=%2Fauthorize%3F…` (a **local** target — an absolute `https://…` target here is the shape that [never came back](/Doc/Architecture/RedirectTargetContract)) → `Issued OAuth authorization code for user …, client {ClientId}` → `Issued OAuth access token for user …, client {ClientId}`. A client whose previous token was superseded then shows exactly that sequence with `authenticated=True` and no `/login` hop, which is the silent re-authorization an evicted session relies on.

---

## Managing Tokens

### Viewing Your Tokens

The **API Tokens** settings page lists every token you own, with the following columns:

| Column | Description |
|---|---|
| **Label** | The name you gave the token |
| **Token ID** | First 8 characters of the hash (for identification) |
| **Created** | When the token was generated |
| **Expires** | Expiration date, or "Never" |
| **Last Used** | Last successful authentication |
| **Status** | Active, Expired, or Revoked |

### Revoking a Token

Click **Revoke** next to any active token to invalidate it immediately. Revoked tokens are permanently unusable — there is no undo. Revoke when:

- A token may have been compromised.
- A team member leaves the project.
- You want to rotate credentials on a schedule.

### Via the REST API

```bash
# List tokens
curl https://your-portal.com/api/tokens -b cookies.txt

# Revoke a token
curl -X DELETE https://your-portal.com/api/tokens/ApiToken/a1b2c3d4e5f6 -b cookies.txt
```

---

## Security Best Practices

> **One token per client.** Create a separate token for each tool or integration so you can revoke access precisely without affecting others.

- **Set expiration dates** — for shared environments, expire tokens after a fixed window (e.g. 90 days) and regenerate on rotation.
- **Revoke unused tokens** — review your token list regularly and revoke anything you no longer actively use.
- **Never commit tokens to source control** — store them in environment variables or a secret manager (e.g. Azure Key Vault, 1Password).
- **Always use HTTPS** — tokens travel in HTTP headers; plaintext HTTP exposes them to interception.

---

## How It Works

```mermaid
sequenceDiagram
    participant Client as MCP Client
    participant Auth as Auth Handler
    participant VUM as Virtual User MW
    participant UCM as User Context MW
    participant MCP as MCP Handler

    Client->>Auth: POST /mcp + Bearer token
    Auth->>Auth: SHA-256 hash token
    Auth->>Auth: Lookup ApiToken node by hash
    Auth->>Auth: Verify not expired/revoked
    Auth-->>UCM: ClaimsPrincipal (userId, name, email)
    Note over VUM: Skipped for /mcp
    UCM->>UCM: Extract AccessContext from claims
    UCM->>MCP: Request with user identity
    MCP-->>Client: MCP response (RLS enforced)
```

When a request arrives at `/mcp`, the pipeline runs in five steps:

1. **`ApiTokenAuthenticationHandler`** extracts the Bearer token from the `Authorization` header.
2. It hashes the token with SHA-256 and looks up the corresponding `ApiToken` node by that hash.
3. If found and valid (not expired, not revoked), it builds a `ClaimsPrincipal` with the token owner's identity.
4. **`UserContextMiddleware`** reads those claims and sets the `AccessContext` on the request.
5. MCP tools execute under the token owner's identity — row-level security is enforced throughout.

Unauthenticated requests are rejected before they reach any MCP handler, so there is no risk of bypassing identity checks by omitting the header.
