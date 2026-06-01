---
NodeType: Markdown
Name: "MCP Authentication"
Abstract: "How to generate API tokens and configure MCP clients like Claude Code to securely access the MeshWeaver MCP endpoint with bearer token authentication."
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

The MeshWeaver MCP endpoint (`/mcp`) uses bearer token authentication. Every MCP client — Claude Code, Cursor, or a custom integration — must include a valid API token in its `Authorization` header. Without one, requests receive `401 Unauthorized` and never reach the MCP handler.

Tokens are personal. Each token is tied to a specific user, so all MCP operations run under that user's identity with full row-level security (RLS) enforced.

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
      "url": "https://your-portal.com/mcp",
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
curl -X POST https://your-portal.com/mcp \
  -H "Authorization: Bearer mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"initialize","params":{},"id":1}'
```

A successful JSON-RPC response confirms authentication is working. A `401` means the token is missing, invalid, expired, or revoked.

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
