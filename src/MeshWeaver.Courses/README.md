# MeshWeaver.Courses

Course delivery, shipped as a module: `GET /assets/{Space}/{path…}` resolves a course asset from
the Space's synced GitHub repository, applies the entitlement gate, and 302-redirects to GitHub's
short-lived tokenized `download_url`. The bytes are never proxied through the portal.

## Activating

```json
{ "Modules": { "Assemblies": [ "MeshWeaver.Courses.dll" ] } }
```

Asset resolution authenticates as the GitHub App installation the GitSync configuration already
provides (`GitHub:App:*`), so private course repos resolve with no per-user credential. A
deployment that hosts no courses simply omits the DLL — and with it the resolver and the route.

## The gate

The viewer needs `Read` on the Space **and**, when the course is paid (its `{Space}/_Entitlements`
container has entries), an entitlement node at `{Space}/_Entitlements/{viewer}`. Course admins
(`Update` on the Space) always pass. Denials are `401` for an anonymous viewer and `403` for an
authenticated one; a Space with no GitSync config, or a file absent from the repo, is `404`.

🚨 The route is mapped `AllowAnonymous` **on purpose**, and must stay that way. Module endpoint
contributions map inside a group that defaults to `RequireAuthorization()`, which would challenge
an anonymous viewer before `CourseAssetGate` ever runs — turning a public course's assets into a
login redirect. The gate is the guard: it distinguishes anonymous-but-allowed from
`NotAuthenticated` itself.

## Contents

- `CourseAssetEndpoints` — the route, reactive end to end, bridged to `Task<IResult>` exactly once
  at the HTTP boundary.
- `CourseAssetGate` — the pure decision logic (path parse, repo-path mapping, entitlement verdict),
  unit-tested in isolation.
- `CourseAssetService` — the GitHub-contents resolver, with a per-file promise cache so concurrent
  requests for one asset share a round-trip.
