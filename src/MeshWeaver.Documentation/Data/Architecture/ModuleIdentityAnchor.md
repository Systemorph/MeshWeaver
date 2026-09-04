---
Name: The Module Identity Anchor
Category: Architecture
Description: A module bundle states the framework build its bytes were compiled against. That identity belongs to the PLATFORM, so it may never come from the module — not from its publish output, and not from a build carrying its package version. The two failure shapes that produced (one red and loud, one green and silent), and where the anchor comes from on each lane.
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
| built from SOURCE (`REFS` empty) | `MeshWeaver.Compiler` **built from the pinned platform source**, in the pack job | core's own `main-cd` call |

The second row is the one this page exists for, and it took two changes to get right — the first
(#3293) fixed the anchor's LOCATION, the second (#3176) fixed the PROPERTIES it is built with. Both
were the same underlying mistake: treating a per-module fact as a platform fact.

🚨 **The build passes `-p:MeshWeaverRoot` and `-warnaserror`, and deliberately NOT
`-p:Version="$VERSION"`.** That is the whole of the second fix, and the reason is in the next
section.

## What reading the module's own output actually did

Until MeshWeaver#3176 the source arm read `$PACKDIR/MeshWeaver.Compiler.dll` — the directory being
packed. The comment beside it asserted the premise plainly: *"the platform ProjectReferences are
real, so MeshWeaver.Compiler.dll IS beside the module in the publish output."* That premise is false
in general, and it fails in two opposite directions.

### Shape 1 — ABSENT, and it stops the fleet (fixed by #3293)

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

### Shape 2 — PRESENT and WRONG, silently (fixed by #3176)

The worse half, and the one that **survived the first fix**. The mechanism is `-p:Version`, and it
reached the anchor by two different routes in turn:

1. **Before #3293** — the copy beside the module had been rebuilt inside that module's own build,
   which passes `-p:Version=<the module's package version>`; MSBuild flows the property to every
   transitively built project.
2. **After #3293** — the arm builds the compiler explicitly from the platform source, which is
   right, but passed `-p:Version="$VERSION"` **on purpose**, reasoning that matching the module
   build's global properties makes it *"the assembly publish would have copied"*.

Route 2 fixes the absence and keeps the divergence. `$VERSION` is the MODULE's package version, and
MSBuild writes it into the assembly's version attributes — part of the metadata the MVID is computed
over. So every matrix job built its own compiler and the run stated one identity **per module**: four
compose entries, four identities, all green.

The result is visible in a single run. Core CD `33874892203` — one platform, one commit, two green
bundles, **two identities**:

```
packed MeshWeaver.Plugin.AI.1.3.18.module.nupkg          … built against framework be27d0fb9ad54ae6a862bfa7aeb97c9b
packed MeshWeaver.Plugin.Essentials.1.0.24.module.nupkg  … built against framework d756b82e09804a11b0ea44d26233af6c
```

Reproduced locally on **#3293's own anchor command**, changing nothing but the version property —
the two package versions core CD actually packs, plus a positive control that runs it twice with no
override:

```
$ dotnet build src/MeshWeaver.Compiler/MeshWeaver.Compiler.csproj -c Release -warnaserror \
    -p:MeshWeaverRoot=<root> [-p:Version=<v>]

-p:Version=1.3.18   (AI)          → 34337c31960d47f5b5251d32ef923fc6
-p:Version=1.0.24   (Essentials)  → 7c1c4f70de084da78659e6bda495e1c5
no override, run A                → 71cc81badb364c5d8558ac5e7db6a44e
no override, run B                → 71cc81badb364c5d8558ac5e7db6a44e   ← identical
```

The control matters as much as the experiment: it is what shows the cure actually cures. Two
independent builds of the same commit with no version override are byte-identical, so removing the
property does not merely change the value — it makes every matrix job in a run agree, which is the
property the field needs and the one it never had.

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

**In the lane guard** (`ModuleIdentityPublishGuard`): the source arm must BUILD its anchor from
`$GITHUB_WORKSPACE/meshweaver/src/MeshWeaver.Compiler` and must never name
`$PACKDIR/MeshWeaver.Compiler.dll` (#3293's ratchet) — **and that build command must not carry
`-p:Version`** (#3176). The second assertion is the one that fails on a lane which has fixed only the
location: it reads the text between the `dotnet build` and the `anchor=` that consumes it, so a
version override reintroduced anywhere on that command line is caught.

## The rule

**An identity is a property of the thing it identifies.** The framework identity belongs to the
framework build, so it is read from something the framework build owns — the pinned image's `/app`,
or a platform publish made once per run. The moment it is read from a per-module directory it
becomes a per-module value, and a per-module value for a platform-wide property is wrong even when
it is present, well-formed and green.

## Why the ledger's `RECIPE_VERSION` did NOT move with this

`module-build-key.py` documents the rule as *"bump on a byte-changing lane edit"*, and #3211 bumped
it to `"2"` when it ADDED `frameworkMvid` to the manifest. This change moves that field's VALUE on
the source arm, so the question is fair — and the answer is that the key already covers it, on both
arms, without a bump:

- **Source arm (the only arm whose bytes change).** The key includes `platformRef`, and on core's
  `main-cd` call `platform-ref` IS the commit being built. Any run after this change carries a
  `platformRef` no pre-change run had, so its key differs and no pre-change bundle can be reused.
  #3293 did not bump it either, for the same reason.
- **Image arm.** Untouched — satellites read the anchor from the pinned image's `/app` exactly as
  before, so their bundles' identity does not change and there is nothing to invalidate.

A bump would therefore invalidate every cached bundle in the fleet — a full rebuild wave in every
repo — to protect against a reuse that cannot happen. Note the asymmetry that makes this worth
writing down: a satellite pins the LANE (`uses: …@<sha>`) and `platform-ref` SEPARATELY and on
purpose, so a lane edit alone does NOT move a satellite's key. That is safe here only because this
edit cannot change what the image arm packs; a future edit to the image arm would need the bump.

## Still open, deliberately not changed here

On the source arm the anchor states a raw MVID, because the anchor build passes no `-p:CIRun=true`
and `FrameworkBuildIdentity.Resolve` falls back to the content identity when there is no
`MeshWeaverFrameworkIdentity` stamp. The portal image for the same commit IS built with
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
