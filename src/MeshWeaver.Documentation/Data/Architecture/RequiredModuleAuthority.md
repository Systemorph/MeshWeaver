---
Name: Required Module Authority
Category: Architecture
Description: Modules:Required is an array, and configuration merges arrays BY INDEX — so a deployment's list cannot say "these and only these", a shorter list leaves the image's tail required, and an empty list requires MORE. The scalar claim that fixes it, why it is opt-in, and the two instruments that make the gap visible.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# Required Module Authority

A `Deployments/<name>` record is meant to be the single authoritative description of an instance.
For required modules it was not, and the reason is a property of ASP.NET configuration rather than
of anything in this repository: **`Modules:Required` is an ARRAY, and configuration merges arrays BY
INDEX.**

A later provider replaces the entries it NAMES and leaves every other index of an earlier one
standing. An array can therefore express *"replace entry N"* and can never express *"these and only
these"*.

## What that does to a record

The record's `requiredModules` is rendered as `Modules__Required__0`, `__1`, … by
[`DeploymentPortalConfig.ModuleEntries`](../ConfiguringAnInstanceFromAspire) into the portal ConfigMap
(or, under Aspire, the container environment). The image's own list ships in
`Memex.Portal.Distributed/appsettings.json`, in another repository. Two providers, in that order.

| The record says | What the instance actually requires |
|---|---|
| a list **longer than or equal to** the image's | the record's list (the intended reading) |
| a list **shorter than** the image's | the record's entries **plus the image's tail**, which the record never names |
| **an empty list** | the image's list **in full** — nothing is rendered, so nothing is overridden |

The last row is the one an operator hits first, and it is the opposite of what they meant: **emptying
`requiredModules` does not relax the requirement, it restores it.**

Measured on `pearl.meshweaver.cloud`, 2026-09-16 (#4476). `Deployments/pearl` named five modules and
never named `Social`; the portal demanded `Social` anyway, because the image's index 5 was never
overridden. The record was then edited to `requiredModules: []` at 06:08:24Z, and the pod created by
the re-provision that followed reported at 06:18:28Z that **five** required modules were still
missing.

## Why "put your entry at the first free slot" is not a fix

The remedy that predated this page — `RequiredModuleSlots`, a boot module at an explicit index past
the image's list — needs the caller to know how long the image's list is. Nothing in the record, the
chart, the Aspire adapter or the operator can read that: it ships from another repository and it
grows.

It has grown. The list was **seven** entries when the shadow check was written and is **nine** today
(`Social`, `Blazor.Chat`, `Markdown.Collaboration` and `AI` were each added because losing one
silently is a measured outage). `Deployments/memex-cloud` still names slot 7 for
`MeshWeaver.Mcp.dll` — and index 7 of today's image is `MeshWeaver.Markdown.Collaboration.dll`, so
Memex#131 has silently recurred on the public instance (measured on the control instance, record
v85, 2026-09-16; tracked as Systemorph/Memex#378). A free slot is not a property of the deployment;
it is a property of an image the deployment cannot see, and an answer that expires silently is not
an answer.

## The claim

A record states that its own entries are the complete set:

```json
{
  "requiredModules": ["MeshWeaver.Blazor.Radzen.dll", "MeshWeaver.Speech.dll"],
  "requiredModulesAuthoritative": true
}
```

which renders one extra key beside the entries:

```text
Modules__RequiredIsAuthoritative = true
```

That key is a **scalar**, so no index merge can touch it — which is the whole point. It is also the
only thing on the wire that an *empty* list can still say: with no entries rendered at all,
"require nothing" and "this record has no opinion" are otherwise the same bytes.

The reading is `MeshBuilderModuleActivation.RequiredEntries(configuration)`, and it is read off
**provider order**, the same mechanism `ShadowedRequired` already used:

- **No claim** — the merged `Modules:Required` array, blanks dropped. Exactly what every caller read
  before, byte for byte.
- **Claim** — the entries supplied by the provider that states it *and anything layered after it*,
  and those only. Indices only an earlier provider supplies are not required here.

No count, no padding, and no knowledge of the image's length. A later provider may withdraw the
claim by setting it to `false`; the renderer therefore never emits `false`, only `true` or nothing.

Both delivery routes carry it, and both carry the same **slots** — the contiguous `requiredModules`
list *and* every explicit `requiredModuleSlots` entry. (The operator's catalog config file used to
render the contiguous list alone and drop the slots, so two renderers of one record described
different required sets. Harmless while both were read as an overlay; beside the claim they would
have stated two different *complete* sets, and the route that dropped a slot would have said the
module is not required at all.)

🚨 **The chart's block has a ceiling, and under the claim an unrendered slot means NOT REQUIRED.**
The portal ConfigMap names every `Modules__Required__N` key literally — a Helm `range` renders
correctly and is invisible to the key-literal guards — so the list stops somewhere;
`DeploymentPortalConfig.MaxRenderedRequiredModuleSlot` states where, a test reads the number back
out of the template so the two cannot drift, and `RequiredModuleProblems` (folded into
`SpecProblems`) *names* a slot above it instead of letting the render drop it.

The boot path and the `/health` `required_modules` check must ask this question the *same* way — a
probe that disagreed with the log line before it would be worse than no probe — so both go through
`RequiredEntries`, and `MissingRequired` is defined in terms of it.

## Why it is opt-in

The image's list is the **platform's floor**, not a default the record replaces: the AI engine, the
chat renderer and the collaboration pack are named there because a portal that lost one rolled out
green and served with the feature simply gone (2026-08-26/27).

Every record in the fleet names **five** modules against the image's **nine**. Taking a partial list
as authoritative *by default* would have un-required four modules on every instance the day it
shipped — a silent loss of exactly the guarding the list exists for. So the claim is explicit, and a
record that does not make it behaves as it always did.

## The two instruments

A claim nobody knows about would leave the default trap live and silent, so the running host reports
both halves of the merge through the boot report (stderr, pre-DI, so it is in the pod log an
operator already has open):

| Instrument | What it sees | The shape it catches |
|---|---|---|
| `ShadowedRequired` | an entry the deployment **replaced** — supplied by an earlier provider, overwritten at its index, and named nowhere else | `Modules__Required__5 = Mcp` over an image whose index 5 is `Social` (Memex#131) |
| `UnstatedRequired` | an effective entry the deployment's own overlay never **reached** | a record naming five over an image naming nine — the instance requires four modules its record does not name (#4476) |

The two are different questions. A replaced module is not MISSING (nobody asks for it any more), so
the readiness contract has nothing to say; an unreached module is not replaced either, so the shadow
check is silent. Both are empty when a deployment states the claim — it stated the whole set, so
nothing is unstated, and replacing the image's leading indices is the *point* — and `UnstatedRequired`
is also empty when only one provider supplies entries at all, so a Monolith, a test mesh or the CLI
never sees the image's own list reported back to it.

## Doing it

- **"These and only these"** — name the complete set in `requiredModules` and set
  `requiredModulesAuthoritative: true`. The slot numbers then stop mattering: `RequiredModuleSlots`
  entries are the record's own and ride inside the set.
- **"Require nothing"** — `requiredModules: []` **plus** `requiredModulesAuthoritative: true`. The
  list alone is inert.
- **"These too, on top of whatever the image requires"** — leave the claim off, and read the boot
  report: it names every module the image requires that this record does not.
- **Blanking** an entry still means "explicitly not required here" and composes with the claim.

Fluent: `record.WithRequiredModules(…).WithRequiredModulesAuthoritative()`.

## Where it is pinned

| Test | Holds |
|---|---|
| `ConfiguredModuleActivationTest` (`test/MeshWeaver.Compiler.Pipeline.Test`) | the reading, over a real two-provider configuration — the short list, the empty claim, the explicit slot, blanking, the no-claim default, the non-root section |
| `RequiredModuleAuthorityTest` (`test/MeshWeaver.Deployment.Contract.Test`) | the rendering — both routes agreeing slot for slot, never-`false`, the JSON round-trip, the ceiling read back out of the chart, and a slot above it reported rather than dropped |

🚨 **The cross-assembly key assertion is in the FIRST of those, not the second, and deliberately.**
The record renders from `MeshWeaver.Deployment.Contract` (zero MeshWeaver references by design — it
ships inside the published Aspire package) and the host reads from `MeshWeaver.Mesh.Contract`, so
the key is spelled twice and a rename on one side would silently stop the other from ever seeing the
claim — rendered-and-never-read is indistinguishable from not rendered. But
`MeshWeaver.Deployment.Contract.Test` references only the renderer's assembly, so the same two
constants compared *there* would be the renderer against a literal: a check that cannot fail for the
reason it exists. `ConfiguredModuleActivationTest` sees both assemblies (through
`MeshWeaver.PluginCatalog`), so the comparison lives there.

## Still open

The `/health` `required_modules` check lives in `Memex.Portal.Distributed` (MeshWeaver.Plugins) and
still reads the raw `Modules:Required` array. Until it adopts `RequiredEntries`, a record that makes
the claim would have the probe and the boot path disagree; for a record that does not make it — every
record today — the two readings are identical. The core half lands first because the satellite
compiles against a pinned core. Tracked as MeshWeaver.Plugins#1963, to be done with the pin bump
that carries this change.

And no fleet record states a complete set yet: `memex`, `memex-cloud` and `pearl` each name five
against the image's nine, and `memex-cloud` additionally shadows one. Restating them is an operator
change on the records in `Systemorph/Memex`, tracked as Systemorph/Memex#378 — the claim makes it
expressible; it does not make it happen.

## See also

- [Modules](../Modules) — what a module is, and the `Modules:Assemblies` / `Modules:Required` split
- [Configuring an instance from Aspire](../ConfiguringAnInstanceFromAspire) — the parity table: method → record field → Helm value → config key
- [The Module Platform Link Gate](../ModulePlatformLinkGate) — the other way a required module can be present and still absent
