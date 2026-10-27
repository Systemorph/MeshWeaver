# MeshWeaver.Cli (`memex`)

Command-line interface for MeshWeaver / Memex. Operates a portal's mesh over its MCP endpoint — every tool the connected instance serves becomes a command, loaded at run time — and builds plugins inside the platform image.

## Run it

Install it as a dotnet tool. Like `gh`, it is a thin client with its own version, released when the
CLI changes rather than with every platform build
([NuGet Package Retirement](../MeshWeaver.Documentation/Data/Architecture/NuGetPackageRetirement.md)):

```bash
dotnet tool install -g MeshWeaver.Cli
dotnet tool update -g MeshWeaver.Cli
```

Or run it from a checkout: `dotnet run --project src/MeshWeaver.Cli -- <command>`.

In CI the same verbs come from the `mw-plugin-test` container image: every lane in this repository
and in every satellite invokes the image's entrypoint directly, so no lane installs the CLI.

## Log in

Create an API token in your portal (Profile → API Tokens), then:

```bash
memex login mw_yourtoken --base-url https://portal.example.com
```

The token and base URL are stored in `~/.memex/config.json`; `$MEMEX_TOKEN` / `$MEMEX_BASE_URL` or `--token` / `--base-url` override per call.

## Building a plugin (`memex build plugin`)

The whole CI contract for a plugin repo, in three lines:

```yaml
- uses: actions/checkout@v7
- run: |
    dotnet run --project "$MESHWEAVER/src/MeshWeaver.Cli" -- build plugin . \
      --image meshweaver.azurecr.io/mw-plugin-test@${{ vars.MW_IMAGE_DIGEST }} \
      --platform-image meshweaver.azurecr.io/memex-portal-ai@${{ vars.MW_PORTAL_IMAGE_DIGEST }}
```

The tool pulls both images, **pins them by digest**, and runs the platform's own builder and tester
inside them. Nothing in the job needs to know about docker, mounts, seeds or allow-files.

### Which images, when the flags are omitted

The image version comes from **configuration**, never from the CLI's own version. Without
`--image` / `--platform-image` the verb runs `<registry>/memex-portal-ai:<tag>` and the tester
from the SAME set, resolved in this order:

1. `$MEMEX_IMAGE_REGISTRY` / `$MEMEX_IMAGE_TAG` / `$MEMEX_IMAGE_MAJOR`
2. `imageRegistry` / `imageTag` / `imageMajor` in `~/.memex/config.json`
3. `cr.meshweaver.cloud`, `latest` and `3`

🚨 **One set, not one string.** A set version (`3.1.11786`) or a line pointer (`3-latest`) names
both repositories as written. The moving `latest` does NOT: CD moves `memex-portal-ai:latest` on the
ARMED set but `mw-plugin-test:latest` at PROMOTION, which can run ahead of arming. So a portal tag
of `latest` pairs with `mw-plugin-test:<major>-latest`, the tester tag CD moves on the same armed
set. The default pair is therefore `memex-portal-ai:latest` + `mw-plugin-test:3-latest`.

```json
{ "baseUrl": "https://memex.meshweaver.cloud", "token": "mw_…",
  "imageRegistry": "cr.meshweaver.cloud", "imageTag": "3.1.11786", "imageMajor": "3" }
```

A flag always wins, and a digest pin from the same CD wave (above) is still what CI should pass.
The tag is used verbatim, so it must be a tag the registry really carries; the verb refuses a
tester/portal pair that does not resolve one framework identity.

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

🚨 **There is no fallback to the tester's `/app`.** Omit `--platform-image` on the command line and
the CLI uses the CONFIGURED portal image (above) — always a `memex-portal-ai` image, never the
tester's subset. A caller that drives `BuildPluginCommand` from code with no platform image at all
is REFUSED before anything is pulled, and passing the tester image as the platform is refused by
name. Pin the
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
| `--image` | the TESTER image (`mw-plugin-test`); pulled and pinned by digest. It executes the build. Default: the configured `<registry>/mw-plugin-test:<tag>` (see *Which images, when the flags are omitted*) |
| `--platform-image` | the PORTAL image (`memex-portal-ai`); pulled and pinned by digest. It supplies the reference set, the framework identity and the runtime. Default: the configured `<registry>/memex-portal-ai:<tag>`; never a fallback to the tester's `/app` |
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
assembly the image already carries. Each dependent compiles against the full transitive output
closure, matching the SDK rather than only exposing its direct project's assembly. See
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

### Mesh commands come from the instance, not from the CLI

The mesh verbs are **not compiled into the CLI**. On each run `memex` reads the connected instance's
MCP tool catalog — `tools/list` on `{base-url}/mcp`, authenticated with the same `mw_…` token
`memex login` stores — and generates one subcommand per tool; the command runs it with `tools/call`.
A tool the server adds or changes is therefore available the moment the instance serves it, with no
CLI release. `memex --help` lists what the connected instance offers.

The catalog is cached per instance URL in `~/.memex/tools/` for 10 minutes. `--refresh` re-reads it.
When the instance cannot be reached the last cached catalog is used and a warning on stderr says how
old it is; with no cache the command fails with exit 2 and names why.

**The mapping rule** (`DynamicCommands`):

| tool schema | command line |
|---|---|
| tool name | the command, kebab-case: `get_diagnostics` → `get-diagnostics`. When two tools share a kebab form (`foo_bar`, `foo-bar`), each keeps its exact MCP name instead — decided over the whole catalog, never by its order |
| any property | an option `--kebab-name` (`basePath` → `--base-path`); its description is the help text |
| `string` / `enum` | a value; an enum accepts only its listed values |
| `integer` / `number` | a number, validated before the call |
| `boolean` | a flag: `--force`, or `--force false` |
| `array` | repeatable (`--tag a --tag b`), or one JSON array |
| `object` / anything without a simple type | a JSON value: `--meta '{"k":1}'` |
| a REQUIRED `string` | ALSO a positional argument, in schema order — `memex get Doc/X`, `memex search 'nodeType:Agent'`, `memex move A/X B/X` |
| the whole argument object | `--json '{…}'` or `--file args.json`; options and positionals given beside it win |
| a property whose option name is reserved (`token`, `base-url`, `json`, `file`, …) or already taken | `--arg-<name>`, then `--arg2-<name>`, … — every property stays addressable |

A missing required argument is refused before anything is sent. Output is the tool's text content,
verbatim, so JSON pipes cleanly into `jq`. Exit codes: `0` success; `1` the tool answered an error
(`isError`, or the mesh's `Error: …` sentence, printed on stderr), a JSON-RPC refusal, or a usage
error; `2` an HTTP or connection failure, or no catalog; `3` anything else (a malformed result, a
timeout). A required property is about PRESENCE: `--json '{"x":null}'` supplies it.

**Compatibility with the verbs the CLI used to hand-code.** The rule above already keeps
`get <path>`, `search <query> [--base-path]`, `move <src> <dst>`, `copy <src> <ns> [--force]`,
`patch <path> --fields '…'`, `recycle <path> [--reason]`, `compile <path>`, `navigate-to <path>`,
`execute-script <path>` and `mirror push|pull <remote> <source>` reading as before. A few shapes the
generic rule cannot express are declared in `DynamicCommands.LegacyShapes`, and each applies only
while the tool still has the named parameter AS A STRING (any other schema takes the generic rule):

| old form | sent as |
|---|---|
| `create -f node.json`, `update -f nodes.json`, `patch <path> -f fields.json` | the file's TEXT as `node` / `nodes` / `fields` (on these three the argument-object file is `--args-file`) |
| `delete <path> <path> …` | `paths` as a JSON array |
| `diagnostics <path>` | the `get_diagnostics` tool |
| `execute-script --timeout N`, `mirror … --target P` | `timeoutSeconds`, `targetPath` |

### Built-in commands

Compiled in, because they are not single tool calls:

| Command | Purpose |
|---|---|
| `login [token] [--base-url]` | Store the API token and base URL in `~/.memex/config.json` |
| `base-url` | Print the portal base URL |
| `build plugin` / `build project` | Build inside a platform image (above) |
| `tests <path>` | Run a node's Tests area and stream its progress until the verdict |
| `upload <path> <file>` | Multipart upload into a node's content collection |

A built-in wins over a server tool of the same name; `memex --help` names any server tool it shadows.

### When the CLI needs a new release

Only when its **transport, auth or built-ins** change — never because a server tool was added,
renamed or given a new parameter (policy `cli-thin-wrapper-own-version`).

### Choosing between `update` and `patch`

`update` is the **full-entity** write and is only correct when you are the sole authority for that
node's content — a one-way sync source, or a buffer that *is* the new content. `patch` is the right
default for "change these fields, leave the rest alone".

Two things neither can do, both by construction:

- **Neither can express a fold.** `count + 1` has to be computed by you from a read, which a second
  writer can invalidate before your write lands. The agent/MCP surface has the same gap; only
  in-process C# (`GetMeshNodeStream(path).Update(lambda)`) folds against the live node.
- **Neither splices text.** Both replace a text field wholesale, so editing one line of a long
  markdown body or code source means re-emitting the document. The anchored `edit_content` tool does
  this, and because the CLI reads the catalog it is `memex edit-content <path> <old-text> <new-text>`.

The full design — the four shapes a write can take and which context may use which — is
`Doc/Architecture/ExpressingAWrite`.

## Learn more

MeshWeaver source and documentation: https://github.com/Systemorph/MeshWeaver — or browse the live docs at https://memex.meshweaver.cloud.
