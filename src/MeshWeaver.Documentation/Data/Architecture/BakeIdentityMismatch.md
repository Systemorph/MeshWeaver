---
Name: Bake Identity Mismatch
Category: Architecture
Description: Why a fully green CD run can publish a bake no portal will ever adopt — the framework identity is an ADDRESS, an assembly attribute is part of it, and -p:Version= is how the two images of one commit stopped sharing one.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M8 12h8"/><path d="M12 8v8"/><path d="M4.5 4.5l15 15"/></svg>
---

# Bake Identity Mismatch

The framework build identity is an **address**, not a checksum. A bake published under `sAAAA…` is
adopted only by a host that resolves `sAAAA…`; publish it under anything else and it is **inert** —
the bundles exist, every publication-side check is green, and every pod compiles the whole shipped
mesh at boot exactly as if no bake had ever run.

This page is the reconstruction of the third time that happened
([#1814](https://github.com/Systemorph/MeshWeaver/issues/1814),
[#1699](https://github.com/Systemorph/MeshWeaver/issues/1699),
[#3022](https://github.com/Systemorph/MeshWeaver/issues/3022)), the measurement that pinned it, and
the rule that keeps the two images of one commit on one address. The mechanism itself —
what the identity hashes and why — is [CI Content Bake](/Doc/Architecture/CiContentBake).

## The symptom

CD's `publish-bake` job fails on its last precondition, *after* `Promote: tag the full set` and
`Verify every image shipped` have both succeeded:

```text
the bake published under: sb0f6a11f5b8e1550e41c9b70e681a395
framework-identity: MISMATCH — the bake published under 'sb0f6a11f5b8e1550e41c9b70e681a395'
but '/portal' resolves 's6dd7f50b68313e3b86801909ebb1507f'.
  Both manifests record every canonical content-surface assembly, so the difference is in the
  recorded HASHES — different binaries (a different architecture, or hosts built from different
  commits).
```

The image set is fine. Only the bake leg fails — and the failure is the check doing its job: it
refuses to publish bundles to an address the shipped portal will never ask for.

## What the identity is a function of

Two hosts compare `mw-plugin-test` (which bakes) against `memex-portal-ai` (which adopts). Each
resolves its own identity from the `meshweaver-surface.manifest` beside its binaries: one
`<AssemblyName>=<SHA-256 of its REFERENCE assembly>` line per MeshWeaver compile reference, hashed
over the canonical `FrameworkBuildIdentity.ContentSurfaceAssemblies` set.

The load-bearing consequence, and the one that keeps being missed:

> **An assembly attribute is part of the reference assembly.** A reference assembly drops method
> bodies and private members — it does *not* drop `[AssemblyInformationalVersion]`,
> `[AssemblyVersion]`, `[AssemblyFileVersion]` or `[AssemblyMetadata]`. Change any of them and the
> reference assembly's bytes change, its SHA-256 changes, the manifest line changes, and the host
> resolves a different address.

That is why `Directory.Build.props` says, in its own words, that `$(Version)` — the run-numbered
string — *"feeds NuGet package versions and Docker image tags ONLY. It must NOT reach any COMPILED
attribute."*

## The measurement

Both images from the failing run were still in the registry, so the two manifests could simply be
read side by side (`docker create` + `docker cp`, no execution):

| | `mw-plugin-test` (bakes) | `memex-portal-ai` (adopts) |
|---|---|---|
| manifest lines | 26 | 46 |
| of the 25 canonical names, present | 25 | 25 |

The line counts differ legitimately — the portal is a bigger app and compiles against more of the
framework — and no canonical name was missing from either side, which is what the error message's
own first sentence says. Of the **25 canonical assemblies present in both, 0 had matching hashes**.

Every one differing — including `MeshWeaver.ShortGuid`, a leaf assembly of a few hundred lines with
no MeshWeaver dependencies at all — immediately falsifies "a surface change": a leaf assembly cannot
have a different API surface in two images built from one commit.

Extracting `MeshWeaver.ShortGuid.dll` from each image and diffing the string table gave the whole
answer in two lines:

```text
mw-plugin-test   …  3.0.0-rc9+779759e77a43000771e457d97e0626f868558448
memex-portal-ai  …  3.0.0-rc9.ci.7478+779759e77a43000771e457d97e0626f868558448
```

Same commit (`CommitHash` and `MeshWeaverFrameworkIdentity` are byte-identical in both), same
architecture (both PDB paths read `obj/container/linux-x64/…`, both manifests taken from the
`linux/amd64` child). One `AssemblyInformationalVersion`, two values.

## The root cause

`main-cd.yml` publishes the two images with different property sets:

```bash
# plugin-test-image — bakes
dotnet publish tools/MeshWeaver.PluginTester/… -p:CIRun=true

# portal-image — adopts
dotnet publish plugins-repo/src/Memex.Portal.Distributed/… -p:CIRun=true \
  -p:Version=3.0.0-rc9.ci.7478
```

The `-p:Version=` was added so the portal image *reports the build it is running*: the portal host
lives in MeshWeaver.Plugins, whose props do not import core's, so `$(Version)` there was the SDK
default and every image was tagged `3.0.0-rc9.ci.<n>` while reporting `1.0.0`. That fix is correct
and stays.

What made it a bug is that `-p:Version=` is a **global** MSBuild property — it applies to every
referenced core project — and core's `Directory.Build.props` derived the compiled version attributes
inside a PropertyGroup guarded on `'$(Version)' == ''`:

```xml
<PropertyGroup Condition="'$(Version)' == ''">   <!-- ← skipped entirely by -p:Version= -->
  …
  <InformationalVersion Condition="'$(CIRun)' == 'true'">$(PlatformVersion)</InformationalVersion>
  <AssemblyVersion>$(_VersionNumeric).0</AssemblyVersion>
  <FileVersion>$(_VersionNumeric).0</FileVersion>
</PropertyGroup>
```

With the group skipped, the three attributes fell through to the SDK's own defaults in
`Microsoft.NET.GenerateAssemblyInfo.targets`, every one of which derives from `$(Version)`:

```xml
<GetAssemblyVersion Condition="'$(AssemblyVersion)' == ''" NuGetVersion="$(Version)" />
<FileVersion Condition="'$(FileVersion)' == ''">$(AssemblyVersion)</FileVersion>
<InformationalVersion Condition="'$(InformationalVersion)' == ''">$(Version)</InformationalVersion>
```

So the escape hatch documented one line above the rule — *"a direct `-p:Version=…` still overrides
everything"* — was the mechanism that broke the rule. `AssemblyVersion` and `FileVersion` happened to
land on `3.0.0.0` either way (the SDK strips the pre-release), which is why only
`AssemblyInformationalVersion` shows in the diff; had the release line's numeric core ever differed,
the [#143](https://github.com/Systemorph/MeshWeaver/issues/143) binding-identity failure would have
come back with it.

The timeline matches to the run. `-p:Version=` landed on `main` at **2026-09-01 15:49**; the bake
job's first identity mismatch is run **33555526073 at 20:28 the same day** — the first CD run after
it that actually built images. Every publishing run since has failed the same way.

### Reproduced, and fixed, in four builds

`MeshWeaver.ShortGuid` — a leaf assembly, one of the 25 canonical names — built four ways with a
pinned `SourceRevisionId`, hashing `obj/Release/net10.0/ref/MeshWeaver.ShortGuid.dll`:

| `Directory.Build.props` | `-p:Version=3.0.0-rc9.ci.7478` | reference-assembly SHA-256 |
|---|---|---|
| before | no | `0274ac17…` |
| before | **yes** | `cf4d622a…` ← **forked** |
| after | no | `0274ac17…` |
| after | **yes** | `0274ac17…` ← equal |

And the same comparison at manifest scale — `tools/MeshWeaver.PluginTester` (the bake host, the one
core project that emits a `meshweaver-surface.manifest`) published twice for `linux-x64`, bare and
with `-p:Version=`, diffing the two manifests:

| `Directory.Build.props` | canonical lines differing |
|---|---|
| before | **26 of 26** — the 25 canonical names plus `MeshWeaver.Hosting.Monolith`, reproducing what the two production images showed |
| after | **0 of 26** |

## The rule

**The compiled version attributes are a function of the COMMIT and of nothing else.** They derive
from `$(PlatformVersion)` plus the SDK's `+$(SourceRevisionId)`, unconditionally — never from
`$(Version)`, and never from a group a caller can switch off.

`Directory.Build.props` now splits the three concerns explicitly:

| group | guarded? | what it decides |
|---|---|---|
| `_VersionNumeric`, `_CiSep` | no | pure functions of `$(PlatformVersion)` |
| `$(Version)` | `'$(Version)' == ''` | the **publishable** string: NuGet version, image tag, `MESHWEAVER_PLATFORM_VERSION` |
| `InformationalVersion`, `AssemblyVersion`, `FileVersion` | **no** | the **compiled** attributes — inputs to the framework identity |

`-p:Version=` therefore still does exactly what CD needs (the portal image reports its own build)
and can no longer move an address. A caller who genuinely wants a different assembly version asks
for it by name — `-p:AssemblyVersion=` / `-p:FileVersion=` / `-p:InformationalVersion=` still win —
so the escape hatch survives, it just cannot be taken by accident.

## What was ruled out, and how

A mismatch has three plausible causes and the error message names two of them. Both were falsified
before the third was accepted:

- **Different commits.** Falsified positively, not by reading the workflow: the `CommitHash` and
  `MeshWeaverFrameworkIdentity` attributes stamped into both images' `MeshWeaver.ShortGuid.dll` are
  the same 40 characters. Both jobs check out `needs.gate.outputs.sha`.
- **Different architectures.** Falsified the same way: both manifests were extracted with
  `--platform linux/amd64`, and both DLLs' embedded PDB paths read `obj/container/linux-x64/`.
- **Different build invocations.** This is a *real* effect — implementation MVIDs differ between any
  two publishes, which is why the platform bakes inside the shipped image rather than from a CI
  build output (see [CI Content Bake](/Doc/Architecture/CiContentBake) §"The identity is a property
  of the BINARIES"). It is not this defect: the full-MVID members are only the toolchain closure,
  and a leaf like `MeshWeaver.ShortGuid` contributes its *reference* hash, which is stable across
  invocations of one commit — as the four-build table above shows.

## The guard

`CompiledVersionAttributesIgnoreVersionOverrideGuard` (`test/MeshWeaver.Documentation.Test`)
evaluates one core project twice — bare, and with `-p:Version=<probe>` — and requires
`InformationalVersion`, `AssemblyVersion` and `FileVersion` to be **non-empty in both and equal
across both**.

Three details make it a guard rather than a green tick:

- **Non-empty is asserted first.** The regression evaluated all three to the empty string (the SDK
  fills them later), so a bare equality check would have compared `""` with `""` and passed.
- **A control arm asserts the override actually landed** — `$(Version)` *must* differ between the
  two runs. Without it the test would pass whenever the `-p:` never reached MSBuild at all.
- **The probed assembly must still be in `ContentSurfaceAssemblies`.** If it leaves that set the
  guard is still measuring a real evaluation, but no longer one that can fork a bake address, so it
  fails and asks to be re-pointed.

The CD step that caught this (`mw-plugin-test framework-identity /portal --expect <baked>`) stays
exactly as it is. It is the artifact-level proof and it must remain able to fail; the guard just
moves the *ordinary* case of this failure from minute 25 of a CD run to second 5 of a PR.

## If you are staring at one of these right now

1. **Read both manifests, do not reason about them.** Pull the two images by their *staging* tags —
   immutable, unlike `main`/`latest` — and `docker cp` `/app/meshweaver-surface.manifest` out of
   each. Publish nothing; this is a read.
2. **Count the differing lines.** *A few* differing lines means a genuine surface difference or a
   missing compile reference (that is [#1814](https://github.com/Systemorph/MeshWeaver/issues/1814)'s
   original shape — eight canonical assemblies absent from the portal's manifest, each hashing as
   `absent`). *All* of them differing means a global input changed: an attribute, the SDK, the
   architecture, or the commit.
3. **Diff one leaf assembly's strings.** `MeshWeaver.ShortGuid` or `MeshWeaver.Reflection` — no
   dependencies, so anything that differs there is global by construction. The `CommitHash` and
   `MeshWeaverFrameworkIdentity` stamps in the same dump settle the commit question in one look.
4. **Never make the check tolerant, and never publish under both identities.** Both turn a real
   address mismatch into a silent one, which is the state this whole page exists to end.

## See also

- [CI Content Bake](/Doc/Architecture/CiContentBake) — what the identity hashes, and why the bake
  runs inside the shipped image
- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — all-or-nothing
  publication, and where `publish-bake` sits in it
- [Rebake Waves](/Doc/Architecture/RebakeWaves) — what a moved identity costs when it moves for a
  legitimate reason
- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — what pods do when they adopt
  nothing
