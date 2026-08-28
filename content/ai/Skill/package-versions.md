---
name: /package-versions
nodeType: Skill
displayName: Package versions — one file, five repos
description: How package versions resolve across the platform and every plugin repo, and how to audit a pin before removing it. Use when adding a PackageReference in a plugin repo, when a module build fails naming only a package, or when pruning Directory.Packages.props.
icon: 📌
category: Engineering
order: 45
---

# Package versions — one file, five repos

**The platform's `Directory.Packages.props` is the ONE source of package versions for every
plugin repo.** The plugin repos declare no `PackageVersion` of their own: their
`src/Directory.Packages.props` exists to say so, and every version resolves from the platform
checkout through `$(MeshWeaverRoot)`.

That is the whole mechanism, and it has one sharp consequence.

## 🚨 Auditing a pin means auditing FIVE repos

A pin with no `PackageReference` in the platform is **not** dead. The extracted modules — the AI
providers, Mail/Graph, the agent SDKs, the Azure backends — live in the plugin repos and reference
their packages from there, while their versions still resolve from the platform's file.

Measured 2026-08-21: an audit that scanned the platform alone called 60 pins orphaned. Fourteen of
them were live satellite dependencies (`Azure.AI.OpenAI`, `Microsoft.Agents.AI.*`,
`Microsoft.Extensions.AI.OpenAI`, `Microsoft.Graph`, `ClaudeAgentSdk`, `GitHub.Copilot.SDK`,
`OpenAI`, …). Removing them turned **nine module-bundle jobs red** on the plugins trunk — and the
failure named only the package, at "Build the module", four steps from the cause.

**Before removing any pin, scan all five repos:**

```bash
python3 - <<'PY'
import re, glob, os
used = set()
for repo in ['~/code/MeshWeaver', '~/code/MeshWeaver.Plugins', '~/code/MeshWeaver.SocialMedia',
             '~/code/MeshWeaver.Reinsurance', '~/code/MeshWeaver.Education']:
    root = os.path.expanduser(repo)
    for pat in ('**/*.csproj', '**/*.props', '**/*.targets'):
        for f in glob.glob(os.path.join(root, pat), recursive=True):
            if any(x in f for x in ('/bin/', '/obj/', '.worktrees', '.claude')): continue
            try: used.update(re.findall(r'PackageReference\s+(?:Include|Update)="([^"]+)"', open(f).read()))
            except Exception: pass
pinned = re.findall(r'PackageVersion Include="([^"]+)"',
                    open(os.path.expanduser('~/code/MeshWeaver/Directory.Packages.props')).read())
print('\n'.join(sorted(p for p in pinned if p not in used)))
PY
```

Three details that make the scan honest:

- **Match `Update=` as well as `Include=`.** Analyzer pins (Roslynator) and similar arrive through
  `PackageReference Update="…"`, which an `Include`-only regex misses.
- **Scan `tools/` and `samples/`, not just `src/` and `test/`.** `SixLabors.ImageSharp` is used by
  `tools/MeshWeaver.ThumbnailGenerator` alone.
- **Scan `.props`/`.targets` too** — a globally injected reference lives there, not in a csproj.

## Transitive pinning is OFF — a CVE override may be doing nothing

`CentralPackageTransitivePinningEnabled` is not set, so a `PackageVersion` only takes effect for a
package something references **directly**. Two pins in the file carry "pinned to override transitive
…" CVE comments (`Microsoft.AspNetCore.DataProtection`, `OpenTelemetry.Api`) and override nothing
today. They are kept as the intent record with that stated plainly; making them effective changes
resolution repo-wide and belongs in its own change.

## Multi-line entries are elements, not lines

Some pins carry child metadata (`IncludeAssets`, `PrivateAssets`). A line-wise edit orphans the
body and produces a file that still *looks* fine and breaks restore in an unrelated project — the
Aspire AppHost failed with "needs a package reference to Aspire.Hosting.AppHost" when its own pin
was untouched. Edit whole elements, and validate the XML afterwards:

```bash
python3 -c "import xml.dom.minidom as m; m.parse('Directory.Packages.props'); print('XML OK')"
```

**The gate for any pin change is a whole-solution restore**, not a project build:
`MW_ALLOW_SOLUTION_BUILD=1 dotnet restore MeshWeaver.slnx`.
