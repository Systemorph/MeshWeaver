---
Name: The Menu Contribution Boundary
Category: Architecture
Description: Which menu entries can be data and which stay compiled — gates subtract, they never select. The settled answer to WS7 ask 4, with the four node-menu defaults that are inexpressible and why the other ten stay compiled anyway.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="8" height="16" rx="2"/><path d="M15 8h6M15 12h6M15 16h4"/></svg>
---

# The Menu Contribution Boundary

[Menus as Data](/Doc/Architecture/MenuAsData) makes a menu entry's *presentation* editable, and
[UI Extensibility](/Doc/Architecture/UiExtensibility) makes an entry itself *contributable* as a
`UiContribution` node. This page draws the line between them and the compiled providers: **what may
be data, what must be code, and why the node menu's own defaults stay where they are.**

## The rule

> 🚨 **Gates SUBTRACT. They never SELECT.** A contribution declares a *fixed* entry — one label, one
> icon, one area, one destination. The closed gate vocabulary decides only **whether it appears**.
> An entry whose label, icon, area, href or action is a *function of state* — the node's or the
> viewer's — stays compiled.

Everything below follows from that sentence. It is the same rule that already keeps behaviour out of
the lane ("New thread" is compiled because its destination is resolved from the circuit's viewer at
click time), extended to the other half: state-dependent *presentation* is not a gate, and a
vocabulary that could express it would stop being closed.

Concretely, a conditional in the data would have to pick a branch when the evidence is missing.
Gates fail closed — an unresolved node means "do not show", which is always safe. A selector has no
safe default: offering **Resume synchronization** on a node that is in fact synced is a *misleading
action*, which is worse than a missing entry, and no amount of care in the data prevents it.

## The four node-menu defaults that are inexpressible

Measured against the vocabulary as it stands (`UiContributionGates`: `NodeTypes`,
`ExcludePartitionRoot`, `AdminOnly`, `SyncedOnly`, `ExcludeViewerHome`, plus a single
`RequiredPermission`):

| Entry | Why it cannot be a contribution |
|---|---|
| **Presentation** (`HideInPresentation` / `ShowInPresentation`) | Label, icon, **area** and tooltip all flip on the *viewer's own profile* (`User.HiddenPaths`). Viewer profile state is not a gate input at all — the vocabulary evaluates node shape, effective permission, admin, and the viewer's partition key. Worse, the compiled provider deliberately `Seeded()`s that stream so a viewer with no `User` node cannot stall the menu; folding it into the shared gate evaluation would put that stall risk behind *every* contribution. |
| **StopSync** | Two independent walls. The label and icon flip on `SyncBehavior` — `SyncedOnly` covers the *Stop* half's shape only, and the inverse is deliberately absent. And its permission gate is `Update` **OR** `Sync`; `RequiredPermission` is one `[Flags]` value checked with `HasFlag`, so an OR is inexpressible whatever happens to the label. |
| **Recycle** | Carries `Action = MenuActions.Recycle` — a command id a renderer runs in place. See the next section: this one is a security wall, not an expressiveness wall. |
| **Create** (Mesh menu) | Its `Href` carries a `?type=` query string **only when** the anchoring node is a `NodeType`. The `{node}` token substitutes a path; it cannot make a segment conditionally present. |

Note that this is four, not the two named when the question was raised — and two of them fail for
reasons that have nothing to do with labels. The count matters: "two special cases" reads like an
exception to sweep up later, and "four, for three unrelated reasons" reads like a boundary.

### 🚨 `Action` must never become a contribution field

`NodeMenuItemDefinition.Action` is a command id, and its own contract states that applicability
stays with the provider that emitted the entry — **nothing downstream re-checks
`RequiredPermission`**. So a contribution able to declare `action: "recycle"` beside
`requiredPermission: Read` would hand every reader of a node a button that tears its hub down.

That is a **widening**, and the one thing the closed vocabulary exists to make impossible. Adding
`Action` would not be a vocabulary extension; it would be a hole. Two ratchets in
`UiContributionProjectionTest` pin it: a projected entry always carries a null `Action`, and
`UiContribution` declares no property that could name a command.

## Why the other ten stay compiled too

Design #1645 planned to retire the node/mesh defaults into a pre-installed pack, leaving a minimal
compiled fallback — Data + Versions + Delete, enough to operate a zero-plugin mesh. Ten of the
fourteen entries *are* expressible in the vocabulary, so this was mechanically possible.
**It is not being done**, for a reason the design could not see until the lane existed:

**For a core node-menu default, the contribution lane buys nothing an admin does not already have,
and costs availability.**

- **Buys nothing new.** The editability half of "menus as data" is already delivered for these
  entries by the `MenuPresentation` catalog: re-word, re-icon, re-order, re-group and **hide**, live,
  no build. That covers what an operator actually wants to do to a built-in entry.
- **Costs availability.** `UiContributionCatalog` is fail-soft to **empty** by design — a faulted
  query degrades to the last known set, and a hub without a mesh data source gets an empty catalog on
  purpose. That is exactly right for a package's front door, and exactly wrong for the menu that is
  how you *edit and delete anything*. Making Edit / Move / Copy conditional on a query succeeding
  trades a guarantee for a convenience nobody asked for.
- **Does not shrink the surface.** Four entries stay compiled regardless, so the provider, its
  permission composition and its node-stream plumbing all survive the migration. The decongestion
  would be ten table rows, not a component.

The lane's value is **additive**: it lets a package ship an entry that could not be compiled into
core at all, and it decongested ~20 settings-tab registrations that genuinely came from many modules.
Core's own node operations are not a package's front door.

> **What this does not say.** The contribution lane is not in question — it ships the platform
> settings tabs, the AI menu's catalog entries, and every package front door. Only the retirement of
> the *node/mesh menu defaults* into it is closed.

### One more thing the retirement would have got wrong

The retirement was framed as a cross-repo change, moving the entries into the PlatformUI pack in
`MeshWeaver.Plugins`. That is not how the shipped slices work: platform entries are seeded from the
assembly that **owns the area** — `PlatformSettingsTabAreas` in core, `AiMenuContributions` in the AI
module — while the pack carries only the *Apps menu declaration*. Splitting an entry from its area
across a repo boundary would create a new silent-break class: a core area rename would leave the
pack's entry pointing nowhere, and neither repo's static check can see both halves. Keep an entry
and its area in one assembly, where `UiContributionSeedValidation`'s `registeredAreas` argument can
check them together.

## Separators are derived, never declared

A contributed separator is not part of the vocabulary, and it should not be: a declared divider
renders next to whatever survives, including nothing.

Dividers are computed **last**, by `NodeMenuItemsExtensions.WithSectionDividers`, from the finished
list — after every provider is merged, after the `MenuPresentation` overlay, after normalization.
Two adjacent entries in different sections get one divider between them; a divider can therefore
never lead, trail, or double, and an empty middle section produces one rule rather than two.

The Node menu declares its section boundaries once (Order 20 and 40, matching the documented bands);
every other context is flat and derives nothing, keeping only the dividers its providers declare
minus the dangling ones. In a banded context an incoming `_separator` is **dropped** and re-derived,
so an in-mesh provider this build cannot recompile cannot reintroduce the defect.

That defect was live in both directions, which is why the derivation is not merely tidier:

- an admin hiding the last entry of a section through `Admin/Menu/Node` left the divider behind it —
  and hiding entries is what the catalog is *for*;
- on a viewer's own home every compiled section-1 entry is suppressed, so no divider was emitted at
  20 — and a contributed or DI-provided entry in that band ran straight into Files with no rule
  between them.

Neither was expressible from inside a single provider, because a provider sees only its own slice
and runs before the overlay. `MenuSectionDividerTest` pins both.

## Deciding where a new entry belongs

| Your entry… | Lane |
|---|---|
| is a fixed link a package ships with its own area | `UiContribution` — [UI Extensibility](/Doc/Architecture/UiExtensibility) |
| is a settings tab from a module | `UiContribution`, `Settings` or `NodeSettings` context |
| needs different wording for the same operator action | Compiled provider |
| runs a command instead of navigating | Compiled provider — `Action` is never data |
| needs a permission the vocabulary cannot spell (an OR, a role) | Compiled provider |
| just needs re-wording, re-ordering, re-grouping or hiding | Neither — edit the `MenuPresentation` catalog ([Menus as Data](/Doc/Architecture/MenuAsData)) |

Related pages: [UI Extensibility](/Doc/Architecture/UiExtensibility) ·
[Menus as Data](/Doc/Architecture/MenuAsData) · [Node Menu](/Doc/GUI/NodeMenu) ·
[Access Control](/Doc/Architecture/AccessControl)
