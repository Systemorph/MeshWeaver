---
name: plugins
description: Wire a MeshWeaver deployment to the plugin catalog — as a CONSUMER (installs plugins from a registry over HTTP, no git credential) or as the REGISTRY (holds the git credential, serves private plugin repos at /api/plugins). Use when a deployment's Plugin Catalog says "not configured", when adding a new plugin source repo, when issuing an install its registry token, when a plugin won't appear or won't install, or when auditing who can read the catalog. Covers the credential-encapsulation model, the Helm keys (and the trap that un-templated keys are silently dropped), token issuance, and the anonymous-registry exposure that a missing token list causes.
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Edit
  - Grep
---

# /plugins — the plugin catalog: registry and consumers

Plugins are folders of mesh nodes in **private** git repos. The point of the design is that **only
one install holds a git credential**.

```
Systemorph/MeshWeaver.Plugins ─┐
                               ├─→ REGISTRY (memex-cloud)  ── GET /api/plugins ──→ CONSUMER
Systemorph/education ──────────┘   holds the ONE credential   POST /api/plugins/files   no git access
```

npm/NuGet-style: the registry has source access, clients just speak HTTP. Model:
[PluginRegistry.md](../../../src/MeshWeaver.Documentation/Data/Architecture/PluginRegistry.md) ·
[Plugins.md](../../../src/MeshWeaver.Documentation/Data/Architecture/Plugins.md).

## Which am I configuring?

| | Registry | Consumer |
|---|---|---|
| Holds git credential | ✅ | ❌ never |
| Helm values | `pluginCatalog.sources` | `pluginCatalog.registryUrl` (or `.registries`) |
| Secret | `PluginCatalog__RegistryTokens` (the list it **accepts**) | `PluginCatalog__RegistryToken` (the one it **sends**) |
| Today | `memex-cloud` | every other deployment |

A registry is also a consumer of itself — `memex-cloud` sets both.

## Consumer wiring

```yaml
pluginCatalog:
  registryUrl: "https://memex.meshweaver.cloud"
config:
  memex_portal:
    # Systemorph-operated deployments track their plugin repos continuously: a fresh install
    # record is seeded opted-in, so a plugin repo's green build lands with nobody clicking Update.
    # Install-time SEED only — flipping it later changes nothing for already-installed packages.
    PluginCatalog__AutoUpdateByDefault: "true"
secrets:
  memex_portal:
    PluginCatalog__RegistryToken: "<the token this install was issued>"
```

Several registries instead of one:

```yaml
pluginCatalog:
  registries:
    - {name: Plugins,   url: "https://memex.meshweaver.cloud"}
    - {name: Education, url: "https://<other-registry>"}
```

## Registry wiring

```yaml
pluginCatalog:
  sources:
    - {name: Plugins,   repoPath: "https://github.com/Systemorph/MeshWeaver.Plugins", ref: main}
    - {name: Education, repoPath: "https://github.com/Systemorph/education",          ref: main}
  # Sources every NEW instance registration is granted automatically (seeded into the instance's
  # Admin/_PluginGrant node — admins can still revoke per instance). Platform repo only; never
  # list private/paid sources here. Empty = registering grants nothing (the strict default).
  defaultGrants: ["Plugins/*"]
secrets:
  memex_portal:
    PluginCatalog__RegistryTokens: ["<token-for-install-a>", "<token-for-install-b>"]
```

`format` defaults to `node-repo` — a folder appears once it carries a `<Folder>/index.json` **Space**
root with a `PluginManifest`. `package-json` is the alternative layout. Sources merge in configured
order; on an id collision the first wins; a failing repo contributes nothing (logged) rather than
breaking the catalog.

## 🚨 A registry with no tokens serves everyone

`PluginCatalog:RegistryTokens` empty ⇒ the registry answers **anonymously**. That is the local-dev /
e2e stub mode. On a production registry it means the full catalog **and every package's file
content** is readable by anyone who knows the URL.

**Audit it — this is a one-line check, run it:**

```bash
curl -sS https://<registry-host>/api/plugins -o /dev/null -w "%{http_code}\n"   # want 401
curl -sS -X POST https://<registry-host>/api/plugins/files \
  -H 'Content-Type: application/json' -d '{"id":"<some-id>"}' -o /dev/null -w "%{http_code}\n"  # want 401
```

`200` unauthenticated = every private plugin repo behind that registry is public to anyone with the
URL. Issue tokens and set `RegistryTokens`.

> As of 2026-08-06 `https://memex.meshweaver.cloud/api/plugins` returns **200 with 28 packages** to an
> unauthenticated caller, including paid course content from `Systemorph/education`. Fixing it means
> issuing each install a token, setting `RegistryTokens` on the registry, and rolling — in that order,
> or consumers lose their catalog in the gap.

Issue a token:

```bash
openssl rand -base64 32
```

Store it in Key Vault (`<env>-PluginCatalog-RegistryToken`) and surface it through the
SecretProviderClass — never in a values file.

## Installing

Platform admin → **Settings ▸ Administration ▸ Plugin Catalog** → **Install**. It is an admin *tab*,
not a browsable Space (a Space partition would correctly deny read to everyone else — that was the
"Access denied on 'Plugins'" bug). The consumer pulls the package files over HTTP and imports them:

- **Content** package → imports its folder into the target partition.
- **Code** package → synthesizes its `NodeType` from the manifest, imports `Source/*.cs` as Code
  children, and requests a release so the mesh compiles the type live. No app rebuild, no NuGet.

Re-installing is an upsert; installing one module never disturbs another in a shared partition.

## Troubleshooting

**"Plugin catalog not configured"** → `EffectiveRegistries` is empty: no `registryUrl` and no
`registries` entry with a URL reached the pod. Check what actually landed, not what the values file
says:

```bash
kubectl -n <env> get deploy memex-portal-deployment \
  -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}={.value}{"\n"}{end}' | grep -i plugin
kubectl -n <env> get configmap memex-portal-config -o jsonpath='{.data}' | tr ',' '\n' | grep -i plugin
```

**The #1 cause: the chart only emits keys it templates.** Before `pluginCatalog` existed in
`values.yaml`, every plugin key except `AutoUpdateByDefault` was silently dropped — which is why
`memex-cloud`'s registry config was hand-applied as raw Deployment env vars that no redeploy
reproduces. If a key isn't in `deploy/helm/templates/memex-portal/config.yaml`, it does not reach the
pod. Verify by rendering:

```bash
helm template t deploy/helm -f <values>.yaml | grep PluginCatalog
```

**401 from the registry** → the consumer's token isn't in the registry's `RegistryTokens`. Both sides
validate through `PluginRegistryTokens`, one shared contract, so a mismatch is a real value mismatch,
not a format one.

**A folder in the repo doesn't appear in the catalog** → it has no `<Folder>/index.json` Space root
with a `PluginManifest`, or its source's `format` is wrong.

**Package installs but the type doesn't work** → it's a Code package; the compile happens live. Check
the NodeType's compile status before suspecting the install.

## Related

- `/new-deployment` — step 6 wires plugins into a fresh deployment
- `Systemorph/Memex` → `docs/new-deployment.md` §6 (private, has the real values)
- [PluginRegistry.md](../../../src/MeshWeaver.Documentation/Data/Architecture/PluginRegistry.md) ·
  [PluginAuthoring.md](../../../src/MeshWeaver.Documentation/Data/Architecture/PluginAuthoring.md) ·
  [PluginUpdateOnGreenBuild.md](../../../src/MeshWeaver.Documentation/Data/Architecture/PluginUpdateOnGreenBuild.md)
