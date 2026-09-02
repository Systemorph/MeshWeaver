# MeshWeaver.Cli (`memex`)

Command-line interface for MeshWeaver / Memex. Operates a portal's mesh over the REST API — read, search, mutate, compile, and mirror mesh nodes from the shell or from scripts.

## Install

```bash
dotnet tool install -g MeshWeaver.Cli
```

## Log in

Create an API token in your portal (Profile → API Tokens), then:

```bash
memex login mw_yourtoken --base-url https://memex.meshweaver.cloud
```

The token and base URL are stored in `~/.memex/config.json`; `$MEMEX_TOKEN` / `$MEMEX_BASE_URL` or `--token` / `--base-url` override per call.

## Building a plugin (`memex build plugin`)

The whole CI contract for a plugin repo, in three lines:

```yaml
- uses: actions/checkout@v7
- run: dotnet tool install -g MeshWeaver.Cli
- run: |
    memex build plugin . \
      --image meshweaver.azurecr.io/mw-plugin-test@${{ vars.MW_IMAGE_DIGEST }} \
      --platform-image meshweaver.azurecr.io/memex-portal-ai@${{ vars.MW_PORTAL_IMAGE_DIGEST }}
```

The tool pulls both images, **pins them by digest**, and runs the platform's own builder and tester
inside them. Nothing in the job needs to know about docker, mounts, seeds or allow-files.

### Two images, two roles

**`--image` EXECUTES; `--platform-image` supplies the reference set.** The tester image's `/app` is a
strict SUBSET of the portal's — 88 vs 219 assemblies on `3.0.0-rc9.ci.7534`; `MeshWeaver.Maps`,
`.AI`, `.ContentCollections.Indexing`, the Blazor and hosting halves exist only in the portal — so a
build that compiled against the tester's could not see what every portal compiles against. Content
binding a portal-shipped assembly then fails with `CS0234 … does not exist in the namespace
'MeshWeaver'`: a CONTENT-shaped failure with an INFRASTRUCTURE cause, on source nobody changed.

Both stages therefore run from a **composed gate host** — the portal's `/app` with the tester CLI
laid beside it, started by the portal image's own `dotnet` — composed by
`.github/scripts/compose-gate-host.sh`, the same file the reusable node-repo lanes fetch. The CLI
embeds that script and runs it; it does not carry a second copy of the rules.

🚨 **There is no fallback to the tester's `/app`.** Omit `--platform-image` and the build is REFUSED
before anything is pulled, and passing the tester image as the platform is refused by name. Pin the
two digests from ONE CD wave: the run asserts, with the tester's own `framework-identity` verb, that
the two images resolve ONE framework identity, and that the bake it produces is keyed to the identity
the platform host resolves.

It runs the two stages the plugin build contract defines, in order:

1. **produce** — `compile /repo … --output /bake`, then assert the bake identity
   (`framework-mvid.txt`) is non-empty. A compile that emitted no bundles still exits 0, so
   "the command returned" is not evidence.
2. **consume** — the gate with `--seed /bake`, standing a mesh up on **those bytes**. Testing the
   baked bytes is a stronger claim than a fused pass: the bytes judged are the bytes that ship.

| option | meaning |
|---|---|
| `--image` *(required)* | the TESTER image (`mw-plugin-test`); pulled and pinned by digest. It executes the build |
| `--platform-image` *(required)* | the PORTAL image (`memex-portal-ai`); pulled and pinned by digest. It supplies the reference set, the framework identity and the runtime. No default, no fallback |
| `--bake-output <dir>` | where the bundles land (default: a temp dir) |
| `--external-modules <dir>` | module DLLs to mount at `/ext` |
| `--source-sha <sha>` | commit stamped into the bake |
| `--allow <file>` | allow-file relative to the plugin path (default `plugin-gate.allow`, used only if present) |

The images are **arguments, not ambients**: which images a plugin is built against decide the
result, so they belong where a reader can see them. A gate that resolves its framework from an
environment variable while advertising a branch name will eventually report a missing type against
the wrong repository — which is exactly what happened on 2026-08-30.

## Building a compiled project (`memex build project`)

Compile a `.csproj` against a MeshWeaver image's own assemblies — **no dotnet SDK, no NuGet
restore, no platform source checkout**:

```bash
memex build project ../MeshWeaver.Plugins/src/MeshWeaver.Import \
  --image meshweaver.azurecr.io/memex-portal-ai:<tag> \
  --extra-refs ./additional-libs --output ./out
```

The `.csproj` is evaluated without MSBuild, and every reference comes from the image's `/app`, its
`.deps.json` and the shared frameworks installed in it. `ProjectReference`s inside the source root
are built first in dependency order; a `ProjectReference` pointing outside it resolves to the
assembly the image already carries. See
[In-Mesh Build and Test](https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/InMeshBuildAndTest.md).

| option | meaning |
|---|---|
| `--image <image>` | image to build against; pulled and pinned by digest. Omit ONLY when this command is itself running inside a MeshWeaver image — there is deliberately no local-SDK fallback |
| `--output <dir>` | where the emitted assemblies land (default: a temp dir) |
| `--root <dir>` | directory mounted as `/repo` (default: the nearest `Directory.Build.props` ancestor) |
| `--extra-refs <dir>` | libraries ADDITIONAL to the platform — the only way to satisfy a `PackageReference` the image does not supply. Repeatable |
| `--accept <construct>` | acknowledge one construct the evaluator cannot reproduce (`target:<Name>`, `embedded-resource`, `conditions`, `razor-css-scope`, `razor-not-compiled`). Repeatable |
| `--accept <construct>` | acknowledge one construct the evaluator cannot reproduce (`target:<Name>`, `conditions`, `embedded-resource`, `embedded-resource:{resx,culture,dependent-upon,manifest-resource-name,build-output}`). Repeatable |
| `--no-warn` / `--allow-warnings` | warnings fail the build (default); `--no-warn=false` or `--allow-warnings` opts out |
| `--no-pull` | use the image the docker daemon already has, for one built locally |

**Razor/Blazor compiles** (2026-08-31): the image ships the SDK's Razor source generator beside the
builder, per RID, and `build-project` runs it for any project whose `Sdk` processes Razor items —
`MeshWeaver.Blazor` (31 `.cs` + 42 `.razor`) builds green against `memex-portal-ai`. What it will
not do quietly is skip a `.razor` file: a project with Razor input and no generator fails by name,
and CSS isolation (`*.razor.css`) needs `--accept razor-css-scope` because the `b-…` scope comes
from an MSBuild task this builder does not run.

🚨 **Nothing is dropped in silence.** A project construct this builder cannot reproduce — an unknown
element, an unevaluatable `Condition`, a `<Target>`, a resource whose SDK manifest name cannot be
matched exactly — FAILS the run naming
the construct and the file, and a `PackageReference` the container does not supply is reported by
name rather than skipped. A silently dropped `Nullable` or `NoWarn` produces a build that looks green
and is not the build the SDK would have produced.

### Before the package is published

The verb ships in `MeshWeaver.Cli`, so `dotnet tool install -g MeshWeaver.Cli` needs a release that
contains it. From a platform checkout it runs directly:

```bash
dotnet run --project src/MeshWeaver.Cli -c Release -- \
  build plugin ../MeshWeaver.Plugins \
  --image meshweaver.azurecr.io/mw-plugin-test:<tag> \
  --platform-image meshweaver.azurecr.io/memex-portal-ai:<same-wave-tag>
```

Same behaviour, no install — useful for trying it on a repo before the tool version is out.

## Commands

| Command | Purpose |
|---|---|
| `get <path>` | Read a node or resource by path |
| `search <query>` | Search the mesh (GitHub-style query, e.g. `nodeType:Agent`) |
| `create -f node.json` | Create a node from a JSON file |
| `update -f nodes.json` | Full-replace update from a JSON array file |
| `patch <path> …` | Partial update of a node's top-level fields |
| `delete <paths…>` | Delete nodes (recursive) |
| `move <src> <dst>` / `copy <src> <ns>` | Move / copy a node and its descendants |
| `upload <path> <file>` | Upload a file into a node's content collection |
| `compile <path>` / `diagnostics <path>` | Compile a NodeType and inspect diagnostics |
| `execute-script <path>` | Run an executable Code node through the kernel |
| `mirror push\|pull <remoteUrl> <source>` | Mirror a subtree between two portals |
| `recycle <path>` | Force a fresh hub initialisation |
| `navigate-to <path>` / `base-url` | Print the browser URL for a path / the portal base URL |

All commands print the server's JSON verbatim, so output pipes cleanly into `jq`. Errors go to stderr with a non-zero exit code.

## Learn more

MeshWeaver source and documentation: https://github.com/Systemorph/MeshWeaver — or browse the live docs at https://memex.meshweaver.cloud.
