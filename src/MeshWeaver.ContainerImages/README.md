# MeshWeaver.ContainerImages

Serves the **OCI Distribution pull surface** from a MeshWeaver portal, proxying container images
from an upstream registry with one credential the portal holds — while each caller authenticates
against the mesh.

## Why

Every satellite repository carried an upstream registry username/password purely so its CI could
`docker login` and pull the platform tester image — *beside* the mesh instance key it already held
for plugin bundles. Two credentials, one job, in every repository. This collapses that to one.

It is the same encapsulation the plugin registry already applies: the source credential lives in the
registry, and consumers present a registry token.

## What it serves

| route | |
|---|---|
| `GET /v2/` | version probe; answers the bearer challenge when unauthenticated |
| `GET /v2/{name}/manifests/{reference}` | tag or digest; multi-arch indexes included |
| `GET /v2/{name}/blobs/{digest}` | streamed, range-capable |
| `GET /v2/{name}/tags/list` | |

**Pull only.** No push, no upload, no delete — those keep going to the upstream, so this can be
switched off without a migration.

## Configuration

```jsonc
{
  "ContainerImages": {
    "Upstream": "myregistry.azurecr.io",
    "Username": "<pull credential>",
    "Password": "<pull credential>",
    // EMPTY MEANS NONE. Without this, one upstream credential becomes an
    // open read proxy for the entire registry.
    "Repositories": [ "memex-portal-ai", "mw-plugin-test" ]
  }
}
```

The mirror is **off** unless `Upstream`, `Username` and `Password` are all present: every route
answers 404 rather than serving partially.

## Wiring

```csharp
services.AddSingleton<IContainerImageAuthenticator, MyAuthenticator>();
services.AddHttpClient<UpstreamRegistryClient>();
services.Configure<ContainerImageOptions>(config.GetSection(ContainerImageOptions.SectionName));

app.MapContainerImages();
```

`IContainerImageAuthenticator` is a seam: this package takes no dependency on any particular
identity system, so a host binds it to whatever already issues its tokens.

## The one hard limit

**A portal cannot serve the image that boots it.** That pull happens before any MeshWeaver process
exists, so a cluster's own boot image must come from the upstream registry directly. Serving CI, and
serving *other* installations, has no such circularity — the constraint is per-instance, not global.

Design and rationale: `Doc/Architecture/ContainerRegistryInMemex`.
