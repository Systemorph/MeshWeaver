---
Name: The Module Identity Anchor
Category: Architecture
Description: A module bundle states the framework build its bytes were compiled against. That identity belongs to the PLATFORM, so it may never be read out of the module's own build output — where it is present only by accident and, when present, is a rebuild carrying the module's own version. The two failure shapes that produced, and where the anchor comes from on each lane.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="5" r="3"/><line x1="12" y1="22" x2="12" y2="8"/><path d="M5 12H2a10 10 0 0 0 20 0h-3"/></svg>
---

# The Module Identity Anchor

**Every module bundle states the framework build its bytes were compiled against, and that value is
a property of the PLATFORM — one value for every module packed in a run. It is read off an
*identity anchor*: a copy of `MeshWeaver.Compiler.dll`, the assembly `FrameworkIdentity` is anchored
on. The anchor is the platform's copy, never the module's own build output.**

The rule reads as pedantry until you see what happens when it is broken, because the module's own
output *sometimes* contains a copy of that assembly — and it is the wrong one, in two different
ways at once.

## Why the bundle states an identity at all

MeshWeaver#3154 made the identity an INPUT TO A DECISION on every installation. A module's version
encodes CONTENT only, so a rebuild of unchanged source against a NEW platform republishes under the
SAME version; without the identity a consumer cannot tell that rebuild from a no-op, and
`ModuleUpdateDecision.Decide` answers `SkipUpToDate` forever. MeshWeaver#3211 therefore made the
field mandatory at the producer: a bundle that cannot say what it was built against is not written
at all. `ModulePackCommand` takes it as `--framework-mvid <identity>` or reads it itself from
`--graph-dll <path to MeshWeaver.Compiler.dll>`.

So the whole mechanism turns on the anchor naming the right assembly. Naming the wrong one does not
fail — it produces a bundle that states an identity confidently, and no consumer can ever match it.

## Where the anchor is, per lane

`node-repo-module-pack.yml` picks the anchor with the SAME expression the build used, so the anchor
and the reference set the bytes were compiled against cannot be two different things:

| the platform is… | the anchor is… | who runs this |
|---|---|---|
| a pinned IMAGE (`platform-image-digest` set) | `$REFS/MeshWeaver.Compiler.dll` — the `docker cp` of the image's `/app`, which the sdk build passes as `MeshWeaverRefs` and the container build compiles inside | every satellite call |
| built from SOURCE (`REFS` empty) | `$RUNNER_TEMP/pack-tool/MeshWeaver.Compiler.dll` — the module-pack tool's publish output | core's own `main-cd` call |

The second row is the one this page exists for. The pack tool is published ONCE per `platform-ref`,
in the `prepare` job, out of the `meshweaver` checkout and with **no module version override**; it
`ProjectReference`s `MeshWeaver.Graph` → `MeshWeaver.Compiler`, so its output carries the platform's
own identity assembly. Every pack job downloads that one artifact, so every bundle of a run reads
the SAME bytes.

That is a load-bearing side effect of a reference graph the lane does not own, so `prepare` asserts
it rather than assuming it — and asserts it **unconditionally**, because the tool is restored from a
cache keyed on `platform-ref`. A check guarded by the cache-miss condition would let a warm cache
restore a tool without the anchor and skip the very check that would have caught it.

## What reading the module's own output actually did

Until MeshWeaver#3176 the source arm read `$PACKDIR/MeshWeaver.Compiler.dll` — the directory being
packed. The comment beside it asserted the premise plainly: *"the platform ProjectReferences are
real, so MeshWeaver.Compiler.dll IS beside the module in the publish output."* That premise is false
in general, and it fails in two opposite directions.

### Shape 1 — ABSENT, and it stops the fleet

A module's publish output carries the identity assembly only if the module's own reference closure
reaches it. In core, `MeshWeaver.Compiler` is referenced by exactly two projects:

```
$ grep -rl "MeshWeaver.Compiler.csproj" src/ --include='*.csproj'
src/MeshWeaver.Compiler.Pipeline/MeshWeaver.Compiler.Pipeline.csproj
src/MeshWeaver.Graph/MeshWeaver.Graph.csproj
```

So whether the anchor exists at all is an accident of what a module imports:

| module | its platform references | reaches `MeshWeaver.Compiler`? | packed |
|---|---|---|---|
| `MeshWeaver.AI` | `MeshWeaver.Graph`, `MeshWeaver.Hosting`, … | ✅ | green |
| `MeshWeaver.Markdown.Collaboration` | `MeshWeaver.Graph`, `MeshWeaver.Blazor` | ✅ | green |
| `MeshWeaver.Maps` | `MeshWeaver.Layout` only | ❌ | **RED** |
| `MeshWeaver.Payments.Stripe` | `MeshWeaver.Mesh.Contract` only | ❌ | **RED** |

Confirmed by publishing `MeshWeaver.Layout` — Maps' only platform reference — on its own: 43 files
in the publish output, no `MeshWeaver.Compiler.dll`.

The two red modules produced, on every core CD run of 2026-09-04
(`33867513503`, `33873443795`, `33873909869`, `33874892203`):

```
##[error]no identity anchor for MeshWeaver.Maps — expected MeshWeaver.Compiler.dll at
'…/src/MeshWeaver.Maps/bin/Release/net10.0/publish/MeshWeaver.Compiler.dll', i.e. in the module's
own build output (no platform image is pinned, so the platform was built from source). …
```

`All selected bundles built` went red, `Plugins: bake + seal the publication for this identity` was
**skipped**, and no module was published for the fleet. Note what this is NOT: the image half of CD
was green throughout — `Promote: tag the full set` and `Verify every image shipped` both passed, and
the heal ledger recorded a complete image set for every one of those commits. Only the module half
stopped.

**Why it appeared exactly then.** Maps and Payments.Stripe had just been added to the compose set,
because the Plugins content binds them and a bake without them fails `CS0246`. Before that, the set
was AI + Markdown.Collaboration — the two modules that happen to reach the compiler — so the defect
had no way to show. It was additionally masked: every run had been dying earlier, on the
`MeshWeaver.Markdown.Collaboration` one-producer FATAL. When MeshWeaver.Plugins#1268 cleared that
mask at 12:48Z, this became the visible blocker within one run.

### Shape 2 — PRESENT and WRONG, silently

The worse half. Where the closure does reach the compiler, the copy beside the module was **rebuilt
inside that module's own build**, which passes `-p:Version=<the module's package version>`. MSBuild
flows that property to every transitively built project, so the platform's identity assembly is
rebuilt under the module's version and its MVID moves with it.

The result is visible in a single run. Core CD `33874892203` — one platform, one commit, two green
bundles, **two identities**:

```
packed MeshWeaver.Plugin.AI.1.3.18.module.nupkg          … built against framework be27d0fb9ad54ae6a862bfa7aeb97c9b
packed MeshWeaver.Plugin.Essentials.1.0.24.module.nupkg  … built against framework d756b82e09804a11b0ea44d26233af6c
```

Reproduced locally, changing nothing but the version property:

```
$ dotnet publish src/MeshWeaver.Plugin.Build/… -c Release                   -o tool-A
$ dotnet publish src/MeshWeaver.Plugin.Build/… -c Release -p:Version=1.3.18 -o tool-B
914d015cd9c04427be1f1f9e9eca0f1e  tool-A/MeshWeaver.Compiler.dll
9b9fa23435b44398ae91244d3ddd02e8  tool-B/MeshWeaver.Compiler.dll
```

A per-module identity names no platform build any consumer can have landed. `ModuleUpdateDecision`
then compares `(version, identity)` against a value that means nothing — which is precisely the
blind spot #3211 was built to close, reopened one layer down. And unlike shape 1 it is GREEN: the
bundle packs, the manifest carries a well-formed 32-hex identity, and every downstream assertion
that the field is non-blank passes.

## The guards

Two, at different altitudes, because the config check and the behaviour check fail for different
reasons.

**In the packer** (`ModulePackCommand`, behavioural): an anchor that resolves inside the module
directory being packed is refused by name — `exit 2`, nothing written — whether it was passed as
`--graph-dll` or found by the default probe. The default-probe arm is the one that matters: without
it the probe reads a module-local copy and the bundle packs green, which is how two identities for
one platform reached a CD run unnoticed. `ModulePackCommandTest` covers both arms, and the fixture
copies a REAL assembly into place so the refusal cannot pass for "the file was not readable".

**In the lane guard** (`ModuleIdentityPublishGuard`): the pack step must name
`$RUNNER_TEMP/pack-tool/MeshWeaver.Compiler.dll` on the source arm and must NOT name
`$PACKDIR/MeshWeaver.Compiler.dll`; and `prepare` must carry the anchor assertion with no `if:` and
no `continue-on-error`.

## The rule

**An identity is a property of the thing it identifies.** The framework identity belongs to the
framework build, so it is read from something the framework build owns — the pinned image's `/app`,
or a platform publish made once per run. The moment it is read from a per-module directory it
becomes a per-module value, and a per-module value for a platform-wide property is wrong even when
it is present, well-formed and green.

## Still open, deliberately not changed here

On the source arm the anchor states a raw MVID, because the pack tool is published without
`-p:CIRun=true` and `FrameworkBuildIdentity.Resolve` falls back to the content identity when there
is no `MeshWeaverFrameworkIdentity` stamp. The portal image for the same commit IS built with
`-p:CIRun=true` (`main-cd.yml`), so it carries the `g<sha>` commit stamp. Making the two agree is a
separate change with fleet-visible consequences — it changes the SHAPE of the identity core's own
bundles state — and it is not required to make the anchor correct, which is what this page is about.
Before #3176 the source arm already stated raw MVIDs and CD sealed green on them
(run `33779812466`), so the shape is not what was blocking delivery.

## Related

- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the one build shape every repo follows.
- [Module Versioning](/Doc/Architecture/ModuleVersioning) — what you author versus what the build derives.
- [Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a CD run promises to publish.
- [CI Content Bake](/Doc/Architecture/CiContentBake) — the surface manifest and the framework identity it carries.
