---
Name: The Platform Compatibility Ladder
Category: Architecture
Description: Platform builds are backwards compatible within a major and a compatibility epoch — plugin bytes built on platform N run unchanged on N+1. The ladder that states it, the floor/ceiling/epoch that bound it, the break catalogue that shows what each platform change does to a compiled plugin, and the checks that prove it on every platform pull request and every promoted image — including what they cannot see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7 3v18"/><path d="M17 3v18"/><path d="M7 7h10"/><path d="M7 12h10"/><path d="M7 17h10"/></svg>
---

# The Platform Compatibility Ladder

**Rule — policy [`platform-backwards-compatibility`](../PolicyNotProse):** platform builds within
one major and one compatibility epoch are backwards compatible. Plugin bytes built against platform
N run **unchanged** on platform N+1. Only a *declared* epoch bump breaks that; a plugin whose floor
(the platform it was produced on) is newer than the running platform is declined **loudly**. The
compatibility is **proven on every platform build, never assumed**.

## The ladder

Each step changes exactly ONE side:

| Rung | Platform | Plugin | What happens | What must hold |
|---|---|---|---|---|
| 1 | P1 | p1 | the plugin was built on the platform it runs on | types, serves |
| 2 | **P2** | p1 | the platform rolls; the plugin's OLD bytes are kept — no rebuild, no re-seal | every p1 reference resolves on P2; p1 types, serves |
| 3 | P2 | **p2** | the plugin is rebuilt against the RUNNING platform | p2's floor is P2 ≤ running |
| 4 | P2 | **p3** | another plugin revision, same platform | as rung 3 |
| 5 | **P3** | p3 | the platform rolls again | as rung 2 |

Rungs 2 and 5 are the ones a platform change can break, and they are what the checks below measure.
Rungs 3 and 4 are the plugin repositories' own builds against the newest sealed set (the running
platform) — a plugin is never built against a platform newer than the one it will run on.

## Floor, ceiling, epoch — the bounds of a plugin's validity

The key and the fields are introduced by the compatibility-key change (MeshWeaver#5672); this page
states what they mean for the ladder.

- **Compatibility key** `c<major:D3>e<epoch:D3>` (e.g. `c003e001`) — what adoption keys on. The
  per-build identity (`s…`/`g…`) is provenance only. Equal key ⇒ adopt, whatever build produced the
  bytes.
- **Floor** — the platform build the plugin was produced on (`producerPlatformVersion` on a bundle,
  `CompiledPlatformVersion` on a NodeType). Running *below* the floor ⇒ declined, naming both versions.
  Absent ⇒ unknown producer ⇒ accepted.
- **Ceiling** — the last platform build a plugin is valid for (`platformCeiling` /
  `PlatformCeiling`). Open by default. A declared break sets the ceiling of everything built on the
  previous epoch to that epoch's last build; running *above* it ⇒ declined, naming both versions, and
  the plugin rebuilt against the new platform (floor = new platform) is adopted instead.
- **Epoch** — `src/MeshWeaver.Compiler/platform-compatibility.json` `"epoch"`, the ONE source (the
  build reads it into `$(PlatformCompatibilityEpoch)`). A normal platform build changes nothing there.

### The break declaration — machine-checked, never implied

A deliberate break bumps `"epoch"` and appends one entry to `"breaks"`:

```json
{
  "epoch": 2,
  "breaks": [
    {
      "epoch": 2,
      "previousEpochCeiling": "3.0.0-ci.9400",
      "reason": "why the break cannot be a forwarder",
      "declaredIn": "#1234",
      "members": [ { "assembly": "MeshWeaver.Mesh.Contract", "member": "MeshWeaver.Mesh.MeshNode::Frobnicate" } ],
      "affected": [ "MeshWeaver.AI" ]
    }
  ]
}
```

`members[].member` is the full type name (`Ns.Type`) or `Ns.Type::Member`, exactly as the check
names it. The pull-request check holds the declaration to the measurement, both ways:

| The check finds | The epoch | Verdict |
|---|---|---|
| no break | unchanged | ✅ green |
| a break | unchanged | 🔴 red — restore the member (an `[Obsolete]` forwarder keeps the old signature), fix the binder, or declare |
| a break, every broken member listed under the new epoch | bumped | ✅ green — the log names each plugin that must rebuild |
| a break, some member NOT listed | bumped | 🔴 red — naming the undeclared members |
| no break | bumped | 🔴 red — a gratuitous bump forces the whole fleet to rebuild |
| anything | moved backwards | 🔴 red |
| anything | the base declares an epoch, the candidate carries no declaration | 🔴 red — the declaration may not be removed |
| a declared member under a different assembly than the break | bumped | 🔴 red — both halves of an entry must match |
| an assembly **binding conflict** (a higher version than the platform carries) | any | 🔴 red — not declarable; it is the loader refusing the bind |

🚨 A red here is answered by **fixing compatibility or declaring the break** — never by a seal, a
pin, an identity gate or a rebuild-everything fallback.

## The checks, and what each one proves

### 1. On every platform pull request — `Platform compatibility: deployed plugin set links against this platform (ladder)`

`dotnet-test.yml` job `platform-compat`, a **need of `Consolidate test results`** with an explicit
fail step there, so a red ladder blocks the merge (skipped or cancelled is not a pass; the one
exemption is the already-green-tree reuse path, as for every gate on that check).

- **Plugin side:** the newest COMPLETED `main-cd` run on `main` whose `promote` job succeeded and that still holds all four module bundles
  (`MeshWeaver.AI`, `.Markdown.Collaboration`, `.Maps`, `.Payments.Stripe`) —
  `.github/scripts/fetch-deployed-plugin-set.sh`. Those are the bytes the fleet self-rolled to. No
  such run ⇒ **red**, never "nothing to check".
- **Platform side:** this pull request's core build (the tester's bin — the canonical content
  surface) plus the runner's shared frameworks.
- **Measurement:** `mw-plugin-test platform-link`, i.e. `ModulePlatformLink.Check` with
  `ModuleLinkOptions.WithMembers` — the ONE probe the boot, landing and roll gates use, with its member
  half switched on. `--judge-from` scopes it to what a core pull request can move: a reference into an
  assembly only the portal host ships (from MeshWeaver.Plugins) is reported unchecked, not refused.
- **Credentials:** none — the run's own token reads this public repository's artifacts, so fork pull
  requests run it too.

Measured when it was written, against main-cd run 36042267665's bundles: 4 module assemblies, 430
type and 850 member references checked, 0 missing.

### 2. On every promoted image — `Compatibility: newest deployed plugin set runs on this platform (ladder)`

`main-cd.yml` job `platform-ladder-compat`: the baseline bundles (the set the fleet ran before this
promote, verified by `satellite-compat-image`) linked against the **promoted portal image** — the full
portal closure, including what the portal host adds. No baseline ⇒ red (the baseline IS the subject
here). Additive: it gates no later CD job.

### 3. The break catalogue — `ModulePlatformMemberLinkTest`

`test/Memex.Portal.Shared.Test/ModulePlatformMemberLinkTest.cs` builds a platform assembly P1 and a
plugin against it with Roslyn, mutates the platform in exactly one way (P2), and asserts TWO columns:
the static verdict, and the **ground truth** — the plugin's P1 bytes loaded next to P2 in a collectible
load context and executed. The static column may never be greener than the runtime one.

| Platform change (P1 → P2) | Caught by | Static verdict | At run time |
|---|---|---|---|
| removed public type | type half | Unlinkable, type named | TypeLoadException |
| renamed / moved namespace | type half | Unlinkable, type named | TypeLoadException |
| removed member | member half | Unlinkable, `Type::Member [signature] (Assembly)` | MissingMethodException |
| changed parameter type | member half (signature) | Unlinkable | MissingMethodException |
| **optional parameter appended** (source-compatible!) | member half (signature) | Unlinkable | MissingMethodException |
| changed return type | member half (signature) | Unlinkable | MissingMethodException |
| static ↔ instance | member half (signature header) | Unlinkable | MissingMethodException |
| member made non-public | member half (accessibility) | Unlinkable, "no longer accessible" | MethodAccessException |
| member made `protected` (still reachable from a DERIVED plugin type, which stays Linkable) | member half (accessibility, in the caller's derivation context) | Unlinkable | MethodAccessException |
| type made non-public | type accessibility | Unlinkable, "no longer public" | TypeAccessException |
| field turned into a property | member half | Unlinkable | MissingFieldException |
| `init` setter turned into `set` | member half (`IsExternalInit` modreq) | Unlinkable | MissingMethodException |
| constructor re-signed | member half | Unlinkable | MissingMethodException |
| member of a generic type re-signed | member half (via the instantiation) | Unlinkable | MissingMethodException |
| enum → string constants, no forwarder | member half (the enum-typed signature) | Unlinkable | MissingMethodException |
| interface member added **without** a default, on an interface a plugin implements | obligation half | Unlinkable, "does not implement" | TypeLoadException when the plugin type loads |
| interface member re-signed (same name and arity) on an interface a plugin implements | obligation half (exact signature) | Unlinkable | TypeLoadException |
| `static abstract` interface member added | obligation half | Unlinkable | TypeLoadException |
| abstract member added to a class a plugin derives from | obligation half | Unlinkable | TypeLoadException |
| base class a plugin derives from made `sealed` | obligation half | Unlinkable | TypeLoadException |
| member / overload / type added | — | Linkable | runs |
| member moved to a base class | member half walks the hierarchy | Linkable | runs |
| interface member added **with** a default implementation | — | Linkable | runs |
| virtual member added | — | Linkable | runs |
| renamed with an `[Obsolete]` forwarder under the old signature | — | Linkable | runs |
| type moved to another assembly behind `[TypeForwardedTo]` | forwarder followed | Linkable | runs |
| minor version bump (3.0.0.0 → 3.1.0.0) | version half: roll-forward | Linkable + advisory | runs |
| **added overload — source side** | *not a binary break* | Linkable | runs; the plugin's **rebuild** (rung 3) hits CS0419 on a parameterless `<see cref>` — fix: spell the cref with its parameter list |
| **behaviour change behind an unchanged signature** | **nothing static can** | Linkable | runs — differently |

The in-class negative control runs the removal case with the member half OFF and requires
**Linkable**; with the member walk's one reporting line neutralised, six of the member-shaped break
cases went red (demonstrated when the suite was written, then restored).

### 4. The decision itself — `PlatformLinkDecisionTest`

`test/MeshWeaver.PluginTester.Test/PlatformLinkDecisionTest.cs` pins every row of the declaration
table above, and that the declaration file is read strictly (absent = none yet; malformed = an error,
never "no breaks").

## What these checks cannot see — stated so they are never read as covering it

- **Behaviour behind an unchanged signature** — a method that now returns something else, a service a
  plugin resolves that is no longer registered in DI, a default that moved, a JSON field or `$type`
  name a plugin reads. The bytes link and run. Only EXECUTING the deployed plugin set on the candidate
  platform can see it — the runtime rung of the ladder (a mesh stepping through the five rungs with
  the compatibility key, typing every NodeType and serving it) lands with the key change
  (MeshWeaver#5672), not here. For MeshWeaver.Plugins' own SUITES, the rung-3 half of that question
  — the plugin rebuilt from source against the candidate and its tests run — is asked on every
  merge-queue entry by `Dependent suites (MeshWeaver.Plugins)` (policy `dependent-suites-gate`,
  [The Cross-Repo Pair Gate](../CrossRepoPairGate) § "The dependent's suites run against the
  candidate"); the DEPLOYED bytes on the candidate stay the runtime rung's.
- **Source-only breaks** (CS0419 on an added overload, a new ambiguity) — they bite at the plugin's
  own rebuild, rung 3, in the plugin repository's CI; the binary is fine.
- **Reflection and string-named calls** — `Type.GetType("…")`, `GetMethod("…")`, dynamic dispatch.
  There is no `MemberRef` to walk.
- **The base class library** — member checks cover the platform's own `MeshWeaver.*` assemblies;
  BCL compatibility is the runtime's contract, third-party drift is the version half's.
- **An implementation gap on a GENERIC interface or base that differs only in a type** — the
  obligation half matches an implementation by exact signature where the owner is not generic (and for
  every explicit implementation), but by name and parameter count where it is: a generic owner's
  signature is written in its own `!0` terms, and substitution is not attempted. It never reports an
  implementation that exists.
- **A type moved between assemblies WITHOUT a forwarder, inside a signature** — the member half
  compares signature types by full name; the move itself is still caught, by the type half, on the
  plugin's own type reference.
- **The NodeType prebuilt-bundle adoption decision** — whether a pod ADOPTS p1's compiled NodeType
  bytes on P2 is the compatibility key's decision (MeshWeaver#5672), not this surface check's.

## Related

- [The Module Platform Link Gate](../ModulePlatformLinkGate) — the probe this reuses; its type and
  version halves, and where it runs at boot, landing and the roll.
- [Module Adoption Policy](../ModuleAdoptionPolicy) — run the newest thing that loads, keep what you
  have until then.
- [The Cross-Repo Pair Gate](../CrossRepoPairGate) — the SOURCE-level half: a public type or member
  removed from `src/` must declare its counterpart.
- [Continuous Delivery Contract](../ContinuousDeliveryContract) — where the CD job sits.
