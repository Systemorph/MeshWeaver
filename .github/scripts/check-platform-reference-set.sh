#!/usr/bin/env bash
# ═══════════════════ THE PLATFORM IMAGE'S CLOSURE, ASSERTED AT THE PRODUCER ═══════════════════
#
# The platform image IS the compiler and the reference set for every module in every satellite
# (Doc/Architecture/ModuleBuildArchitecture). Until this gate existed NOTHING said what that
# reference set contains: every consumer discovered it by failing to compile against it, hours
# later, in a repository that had changed nothing. Two measured instances, both fleet-wide, both
# from the SAME day:
#
#   * MeshWeaver#3327 — ci.7755's /app carried MeshWeaver.Markdown.Collaboration.dll (and named it
#     in the surface manifest) while the Essentials package composes that same name as a MODULE.
#     Two builds of one assembly name in one bake. The bake's own one-producer check
#     (BakeHost.ShippedByHostProblem, #3175) does say so — but it runs AFTER promote, so the image
#     already carries a version tag every satellite can pin, and the red lands in THEIR repository.
#     Plugins#1268 moved the views into the module and the image stopped carrying it at ci.7758;
#     nothing between those two builds could have said which of them was publishable.
#
#   * MeshWeaver#3328 — Plugins#1284 gave the first-run wizard a SQLite default, which put
#     Microsoft.Data.Sqlite (→ SQLitePCLRaw.core 2.1.11.2622) into /app for the first time at
#     ci.7758, beside a Roslyn assembly that has always bound SQLitePCLRaw.core 3.0.2.2801:
#     Microsoft.CodeAnalysis.Workspaces.Common 5.9.0 declares NO SQLitePCLRaw dependency (its
#     optional SQLite persistence is compiled in with the package dependency excluded), so NuGet
#     cannot see the second consumer at all and resolved the family on the first consumer alone.
#     Neither half is wrong; the PAIR makes /app an internally unresolvable reference set, and
#     every module bundle compiled against it dies on `error MSB3277` under the lane's
#     -warnaserror — in every satellite, with no change in any of their repositories.
#
# 🚨 NO SKIP TRAPDOOR, and it never tests its own inputs by SKIPPING on them: every input is
# asserted and a missing one is RED, naming what to provision. GitHub paints a skipped step the
# same colour as a passed one, and a gate that cannot fail is not a gate.
#
#   check-platform-reference-set.sh <app-dir> <cd-workflow>
#
#     <app-dir>     the platform host's application directory — the publish output that BECOMES
#                   /app in the image (PublishContainer lays the same @(ResolvedFileToPublish)
#                   list down at the container app path), or an extracted /app from a pulled
#                   image. The two shapes are the same directory.
#     <cd-workflow> this repository's main-cd.yml. The composed-module set is READ OUT OF IT
#                   (jobs.plugins-modules.with.modules) rather than restated here: the compose set
#                   and the set this gate forbids in /app must be one list or they drift, and a
#                   drifted second list is how a gate passes while naming the wrong names.
set -euo pipefail

app="${1:?usage: check-platform-reference-set.sh <app-dir> <cd-workflow>}"
workflow="${2:?usage: check-platform-reference-set.sh <app-dir> <cd-workflow>}"

# ─────────────────────────── 0. THE INPUTS, ASSERTED (never skipped) ───────────────────────────
[ -d "$app" ] || { echo "::error::the platform host's application directory '$app' does not exist. This gate reads the bytes that become /app; without them it verifies nothing, so it refuses rather than passes. If the publish layout moved, re-point the caller — never make this conditional."; exit 1; }
[ -f "$workflow" ] || { echo "::error::the CD workflow '$workflow' does not exist, so the composed-module set cannot be read. Point this at .github/workflows/main-cd.yml."; exit 1; }

shopt -s nullglob
dlls=("$app"/*.dll)
n=${#dlls[@]}
# 50 is the floor node-repo-module-pack.yml already uses for "is this a platform image at all"
# (measured: the portal ships 214 assemblies at ci.7794, the tester 88). Below it, the directory
# is not the thing under test and a verdict over it would mean nothing.
[ "$n" -ge 50 ] || { echo "::error::'$app' holds $n assemblies at its root — that is not a platform reference set (the portal ships ~214). Refusing to report a verdict over a directory that is not the thing under test."; exit 1; }

surface="$app/meshweaver-surface.manifest"
[ -f "$surface" ] || { echo "::error::'$app' has no meshweaver-surface.manifest beside its assemblies, so it is not a platform host directory. MeshWeaverSurfaceManifest.targets adds it to @(ResolvedFileToPublish) and PublishContainer consumes that list, so an absent manifest is EITHER this gate pointed at the wrong directory OR the manifest silently stopped being published — which on its own breaks NodeType bake adoption (#1699). Both are RED."; exit 1; }
surface_lines=$(grep -c '=' "$surface" || true)
[ "${surface_lines:-0}" -ge 20 ] || { echo "::error::the surface manifest at '$surface' names only ${surface_lines:-0} assemblies; the platform's MeshWeaver.* compile surface is ~44. A truncated manifest would make the one-producer half compare almost nothing."; exit 1; }

echo "platform reference set: $n assemblies at '$app'; surface manifest names $surface_lines"

fail=0

# ─────────────── 1. ONE PRODUCER — a composed module's name must not be in /app ───────────────
# The set is READ FROM main-cd.yml, not restated: `modules:` on the plugins-modules job is what
# this run actually composes with --module, and it is exactly the set the bake's
# BakeHost.ShippedByHostProblem tests — a whole publication later. Running the same predicate here
# turns a fleet-wide red into a refusal to promote.
composed="$(python3 - "$workflow" <<'PY'
import json, sys
import yaml

doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
job = (doc.get("jobs") or {}).get("plugins-modules") or {}
raw = (job.get("with") or {}).get("modules")
if not raw:
    sys.exit("jobs.plugins-modules.with.modules is absent or empty")
entries = json.loads(raw)
names = sorted({e["module"] for e in entries if e.get("module")})
if not names:
    sys.exit("jobs.plugins-modules.with.modules names no modules")
print("\n".join(names))
PY
)" || { echo "::error::could not read the composed-module set out of '$workflow' — see the message above. This gate refuses to fall back to a hard-coded list: a second list is how a gate ends up asserting the wrong names while staying green."; exit 1; }

composed_count=$(printf '%s\n' "$composed" | grep -c . || true)
[ "${composed_count:-0}" -ge 1 ] || { echo "::error::the composed-module set read from '$workflow' is empty, so this half would pass having compared nothing."; exit 1; }
echo "one-producer: $composed_count composed module(s) read from $workflow — $(printf '%s' "$composed" | tr '\n' ' ')"

while IFS= read -r name; do
  [ -n "$name" ] || continue
  in_app=0; in_surface=0
  [ -f "$app/$name.dll" ] && in_app=1
  grep -qxF -- "$name" <(cut -d= -f1 "$surface") && in_surface=1
  if [ "$in_app" = 1 ] || [ "$in_surface" = 1 ]; then
    where=""
    [ "$in_app" = 1 ] && where="$name.dll in the host's closure"
    [ "$in_surface" = 1 ] && where="${where:+$where and }an entry in its surface manifest"
    echo "::error::ONE PRODUCER (#3175/#3327): '$name' is composed with --module by this run AND shipped by the platform host at '$app' — $where. That is two builds of one assembly name in one bake: the records the bake writes name the module's build (mvid:…) while a portal carrying the name in its app closure resolves the platform's (ref:…), so EVERY NodeType binding it is DECLINED at adoption (\"dependency record mismatch\") on every portal of every satellite. A module has exactly one producer. Remove the assembly from the platform host's closure — code that binds a module belongs IN the module, and a MeshModuleClosure seed row keeps it in the image without making it a second producer (Plugins#1268) — or stop composing it."
    fail=1
  fi
done <<< "$composed"
[ "$fail" = 1 ] || echo "one-producer: OK — no composed module's name is in the host's closure or surface manifest"

# ──────── 2. THE SET MUST RESOLVE — no assembly may demand a version /app does not ship ────────
# 🚨 This does NOT model MSBuild's binding rules, it RUNS them. A hand-written version comparison
# would be a second opinion about what ResolveAssemblyReferences does, and the entire defect class
# here is two artefacts that were each individually plausible. So the gate builds a throwaway
# project whose references ARE this directory, with the module lane's own flags
# (`dotnet build -c Release -warnaserror`, over `-p:MeshWeaverRefs=<the image's /app>`), and
# demands the silence a module bundle demands.
probe="$(mktemp -d)"
trap 'rm -rf "$probe"' EXIT
# Neutral outer files so nothing above the temp directory can lend the probe a property.
printf '<Project />' > "$probe/Directory.Build.props"
printf '<Project />' > "$probe/Directory.Build.targets"
printf '<Project />' > "$probe/Directory.Packages.props"
printf 'internal static class PlatformReferenceSetProbe { }\n' > "$probe/Probe.cs"
cat > "$probe/Probe.csproj" <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Probe.cs" />
  </ItemGroup>
  <!-- Every assembly of the platform host's closure, as a PRIMARY reference — which is exactly how
       node-repo-module-pack.yml's SDK leg hands it to MSBuild. -->
  <Target Name="PlatformRefs" BeforeTargets="ResolveAssemblyReferences">
    <ItemGroup>
      <_PlatformDll Include="$(RefsDir)/*.dll" />
      <Reference Include="@(_PlatformDll)"><HintPath>%(FullPath)</HintPath></Reference>
    </ItemGroup>
  </Target>
  <!-- Anti-vacuity: a probe that resolved nothing would report silence having compiled against
       nothing. The count RAR actually produced is written out, and the caller asserts it. -->
  <Target Name="PlatformRefCount" AfterTargets="ResolveAssemblyReferences">
    <WriteLinesToFile File="$(RefCountFile)" Lines="@(ReferencePath->Count())" Overwrite="true" />
  </Target>
</Project>
PROJ
countfile="$probe/refcount.txt"
log="$probe/probe.log"
set +e
dotnet build "$probe/Probe.csproj" -c Release -warnaserror \
  -p:RefsDir="$(cd "$app" && pwd)" -p:RefCountFile="$countfile" > "$log" 2>&1
rc=$?
set -e

resolved=0
[ -f "$countfile" ] && resolved=$(tr -d '[:space:]' < "$countfile")
if [ "${resolved:-0}" -lt 50 ]; then
  echo "::error::the reference-set probe resolved ${resolved:-0} references from '$app', though the directory holds $n assemblies. A probe that resolved nothing would report silence having checked nothing — the one thing a gate in this family may not do. The probe's own output:"
  sed -n '1,200p' "$log"
  exit 1
fi
echo "reference-set probe: ResolveAssemblyReferences resolved $resolved references from the host's closure"

if [ "$rc" -ne 0 ]; then
  echo "::error::THE PLATFORM IMAGE'S REFERENCE SET DOES NOT RESOLVE (#3328). '$app' holds an assembly that binds a HIGHER version of another assembly in the same directory than the copy shipped beside it, so ResolveAssemblyReferences cannot bind it. This directory is what every satellite's module bundles compile against (Doc/Architecture/ModuleBuildArchitecture) and that lane builds with -warnaserror, so this fails EVERY module in EVERY satellite with no change in any of their repositories. Fix the image's own package graph — pin the demanded version where the family is resolved, or keep the second consumer out of the closure. Do NOT silence the diagnostic at the consumer. The probe's own output:"
  sed -n '1,200p' "$log"
  fail=1
else
  echo "reference-set probe: OK — the closure resolves with no conflict under -warnaserror"
fi

if [ "$fail" != 0 ]; then
  echo "::error::the platform host's closure at '$app' is not publishable — see the errors above. Doc/Architecture/PlatformImageClosure states both invariants, how each was measured, and what the two known breaches were."
  exit 1
fi
echo "platform reference set: OK — $n assemblies, $resolved resolved references, $composed_count composed module(s) absent from the closure"
