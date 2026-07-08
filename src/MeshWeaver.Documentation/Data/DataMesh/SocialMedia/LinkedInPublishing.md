---
Name: LinkedIn Publishing — Setup
Category: DataMesh
Description: Set up publish-to-LinkedIn and engagement from the mesh — the LinkedIn OAuth app, the w_member_social scope, portal config, connect/re-authorize, and publishing a post from a SocialMediaPost node
Icon: <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" rx="4" fill="#0A66C2"/><path fill="#fff" d="M8 19H5v-9h3zM6.5 8.25A1.75 1.75 0 1 1 8.3 6.5a1.78 1.78 0 0 1-1.8 1.75zM19 19h-3v-4.74c0-1.42-.6-1.93-1.38-1.93A1.74 1.74 0 0 0 13 14.19a.66.66 0 0 0 0 .14V19h-3v-9h2.9v1.3a3.11 3.11 0 0 1 2.7-1.4c1.55 0 3.36.86 3.36 3.66z"/></svg>
---

# LinkedIn Publishing — Setup

Publish LinkedIn posts — and pull their engagement — **directly from the mesh**. A [SocialMediaPost](/Doc/DataMesh/SocialMedia) node is drafted in the portal, published to LinkedIn as you, and the returned post URN plus like/comment counts are written back onto the node.

The code lives in the **`MeshWeaver.Social`** module (`LinkedInPostsApi`, `LinkedInPublishService`) with the HTTP surface in `memex/Memex.Portal.Shared/Social` (`LinkedInConnectEndpoints`, `LinkedInPublishEndpoints`, `SocialPostMenuProvider`).

> **Scope of this page:** publishing (writing posts + reading engagement on posts you publish). It does **not** cover reading historical post *impressions* or DMs — see **Limitations** at the end of this page.

---

## 1. Prerequisites — a LinkedIn app with `w_member_social`

You need a LinkedIn Developer app (<https://www.linkedin.com/developers/apps>) that has been granted the **`w_member_social`** OAuth scope — *"Create, modify, and delete posts, comments, and reactions on your behalf."*

- **Products tab** → the *Share on LinkedIn* / *Sign In with LinkedIn using OpenID Connect* products (these carry `w_member_social`, `openid`, `profile`, `email`). Approval is per-app and can take a review cycle.
- **Auth tab** → note the app's **Client ID** and **Client Secret**, and add the **redirect URL**: `https://{your-portal-host}/connect/linkedin/callback`.

`w_member_social` is enough to publish. It is **not** part of general Marketing Developer Platform access, and MDP is *not* required for publishing.

---

## 2. Portal configuration

The portal reads the LinkedIn app credentials from configuration (backed by Key Vault in the deployed portals — see [Deployment](/Doc/Architecture/Deployment)):

| Key | Value |
|---|---|
| `Social:LinkedIn:ClientId` | the app's Client ID |
| `Social:LinkedIn:ClientSecret` | the app's Client Secret |

The requested OAuth scope is fixed in code at `openid profile email w_member_social` (`LinkedInConnectEndpoints`). Widening the scope only takes effect for a user on a **fresh consent** — see the next step.

---

## 3. Connect / re-authorize

Publishing runs under a stored per-user credential. To grant `w_member_social`, the user visits:

```
GET /connect/linkedin?profile={profilePath}
```

(or the **"Link LinkedIn account"** / **"Re-authorize"** item on the credential node's menu). LinkedIn shows a consent screen — approve the new **"create posts on your behalf"** line. On callback the portal stores an `ApiCredential` at `{profilePath}/_ApiCredentials/linkedin` holding the `AccessToken`, the granted `Scope`, and the LinkedIn member id (`SubjectId`, the `sub` claim → `urn:li:person:{sub}`, the post author).

> 🚨 **Existing tokens must be refreshed.** A credential connected before `w_member_social` was added lacks the scope; publishing fails closed with `missing-w_member_social-reconnect` until the user re-runs `/connect/linkedin`.

---

## 4. Publish a post

1. Create a [SocialMediaPost](/Doc/DataMesh/SocialMedia) node with `platform = LinkedIn` and some `Text`/`Body` (`Status` starts as `Draft`).
2. On the node's menu, click **"Publish to LinkedIn"** (`GET /linkedin/publish?postPath={postPath}`).

`LinkedInPublishService` reads the caller's credential, then calls the LinkedIn **Posts API**:

- `POST https://api.linkedin.com/rest/posts`
- headers `Authorization: Bearer {token}`, `LinkedIn-Version: 202506`, `X-Restli-Protocol-Version: 2.0.0`
- body: `author = urn:li:person:{sub}`, `commentary = {text}`, `visibility = PUBLIC`, `lifecycleState = PUBLISHED`

The created post URN (from the `x-restli-id` response header) is written back to the node as `PublishedUrn`, with `Status = Published`. Non-2xx responses surface LinkedIn's status + body verbatim.

---

## 5. Refresh engagement

Once published, the node menu swaps to **"Refresh engagement"** (`GET /linkedin/engagement?postPath={postPath}`), which reads the post's `PublishedUrn` and calls `GET https://api.linkedin.com/rest/socialActions/{urn}`, writing the `likesSummary.totalLikes` and `commentsSummary.count` back onto the node.

---

## 6. How access is enforced

Every publish runs under the **caller's `AccessContext`** — never system-impersonated — with two gates checked *before* any LinkedIn call (see [Access Control](/Doc/Architecture/AccessControl)):

| Gate | Permission | Prevents |
|---|---|---|
| Post | `Update` on the `SocialMediaPost` node | publishing a post you can't edit |
| Credential | `Read` on `{profile}/_ApiCredentials/linkedin` | borrowing another profile's LinkedIn token |

A missing `w_member_social` scope short-circuits with a friendly reconnect prompt and makes **no** HTTP call.

---

## Limitations

- **Text-only.** Image/media upload uses LinkedIn's separate binary-upload flow and is not yet wired.
- **No post-impression analytics via API.** LinkedIn closed the read-posts scope broadly; per-post *impressions* are only in the **archive export** (a user's "Download your data"). Live engagement is limited to like/comment counts on posts you published.
- **No direct messages.** The LinkedIn Messaging API is partner-gated; DMs cannot be sent or read from the mesh.
