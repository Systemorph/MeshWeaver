---
Name: Writing UI for Every Renderer
Category: GUI
Description: How to author layout areas and controls so they render on Blazor, Next.js (portal-next), React Native, and MAUI alike — what auto-inherits, the parity ratchets, the new-control checklist, and the known per-renderer gaps.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="13" height="9" rx="1"/><rect x="16" y="8" width="6" height="11" rx="1"/><path d="M6 17h5M8.5 13v4"/></svg>
---

MeshWeaver UI is **server-driven**: a layout area computes a tree of typed controls in a per-node
hub, and every client renders that tree — Blazor Server, the Next.js shell (portal-next), the React
Native app, and MAUI. Because the tree is the contract, *most UI work inherits into every client
automatically*. This page is the author's guide to keeping it that way: what you get for free, the
rules that preserve it, and the exact checklist when you add a new control.

The architecture of the extension lanes is [UI Extensibility](/Doc/Architecture/UiExtensibility);
the layout-area procedure itself is the [/layout-area](/Skill/layout-area) skill.

## What auto-inherits — and why

The JS shells are not ports of the Blazor portal. They subscribe to the same live streams the
Blazor renderer uses: a `SubscribeRequest` with a `LayoutAreaReference` yields versioned frames
(full, then JSON patches), and interactions flow back as the same small event set. Everything that
lives *in the control tree* therefore renders everywhere with zero client work:

- layout areas, containers, grids, tabs, markdown, DataGrids, forms and their two-way bindings;
- dialogs and click actions (`WithClickAction`, `DialogControl`);
- **menus** — the node/mesh/AI menus are `$Menu:*` areas *inside the same stream*, i.e. protocol
  data, not client code. This is the model case: chrome expressed as controls needs no parity work,
  ever.

What does **not** inherit is anything outside the tree: shell-level access gates, and any semantics
a client must implement per control (see the parity section).

## The five rules

1. **Controls only, never markup.** `Controls.Html` with hand-built markup renders as an opaque
   blob on the JS shells at best. Structured data goes through `Controls.DataGrid`, composition
   through `Controls.Stack` / `Controls.LayoutGrid` — the same rule the
   [/layout-area](/Skill/layout-area) skill states for Blazor, with double force here.

2. **Geometry and derivation live in the control, not the view.** If a visual needs computed
   layout, compute it server-side into the control record so every renderer draws the *result*.
   The analysis controls are the precedent: `TowerControl.Layout()` resolves the band geometry in
   `MeshWeaver.Layout`, and the Blazor, React, and MAUI views all render the same resolved rows.
   A view that derives geometry client-side has to be written four times — and will drift.

3. **Check semantic parity before relying on advanced control behaviour.** The parity ratchets
   (below) guarantee every control *type* has a renderer in every pack — they cannot guarantee
   every *behaviour*. Real example: `MarkdownEditorControl.AutoSaveAddress` was implemented in
   Blazor and silently ignored by the JS packs — edits in auto-save views did not persist there,
   and nothing failed. When your area depends on a control's write-path or side-effect semantics,
   verify the JS pack implements it (or file the gap) before shipping.

4. **Localize through the catalog — and mirror the keys.** Server-rendered text uses
   `host.Localize("key")` with the key in both `strings.en.json` and `strings.de.json`
   (`src/MeshWeaver.Messaging.Hub/Localization/`). 🚨 Every new key has a **second home**:
   `clients/react/src/i18n/` bundles a byte-identical copy so the JS shells resolve synchronously,
   and `localize.test.ts` is a drift guard asserting key-and-value equality — add a key server-side
   only and the `Clients` workflow goes red while a JS client would render the raw key. Prefer
   glyphs over words where possible, and see [Localization](/Doc/Architecture/Localization) for the
   `[Translation]` attribute path for declaration-bound text.

5. **Stay inside the interaction surface.** The events every client speaks are: click, blur,
   close-dialog, and field edits (a JSON patch against the bound pointer). An interaction that
   needs more than these — drag, keyboard chords, scroll observation — is a *framework/pack*
   feature, not something a layout area can express portably.

## Known per-renderer gaps (as of 2026-08)

Be honest with yourself about these when designing a view:

| Capability | Blazor | portal-next / RN |
|---|---|---|
| Monaco completions + live diagnostics | yes | value binding only — no LSP yet |
| CollaborativeMarkdown annotations (accept/reject, threads) | yes | read-only render |
| `AutoSaveAddress` editors | yes | gap — tracked, verify before relying on it |
| Content bytes (`/api/content`) behind auth in `<img>` | cookie session | needs the signed-URL work |

Every control *type* renders on every shell — the packs pass a zero-missing, zero-placeholder
ratchet — so the gaps above are semantic, not structural, and each is on the universal-protocol
work list.

## Adding a new control: the checklist

A new control is not done when the Blazor view renders. The definition of done:

1. **The control record** in `MeshWeaver.Layout` (or your view pack): an immutable record deriving
   from `UiControl`, with any visual derivation as a server-side method on the record (rule 2). Add
   a `Controls.X(...)` factory only if the control is framework-level.
2. **Serialization registration**: `config.WithType(typeof(XControl))` wherever the views register
   — an unregistered `$type` degrades to an untyped `JsonElement` and renders empty.
3. **The Blazor view**: via the pack seam
   (`AddViews(l => l.WithView<XControl, XView>())`) for packs, or the core registry for
   framework controls.
4. **The React renderer**: a component in `clients/react/src/controls/` wired into the render
   registry. The Blazor-parity ratchet (`clients/react/src/render/parity.test.ts`) scrapes the
   core registry's control list and **fails on any missing or placeholder entry** — a new core
   control turns the `Clients` workflow red until the React side exists. That is deliberate: the
   ratchet is what keeps "renders everywhere" true. React-side patterns:
   [React](/Doc/GUI/React) · [React Custom Controls](/Doc/GUI/ReactCustomControls).
5. **The React Native pack**: the `rnPack` entry plus its own parity test
   (`clients/react-native/src/parity.test.ts`). RN consumes the shared renderer core, so most
   controls are a thin mapping; native-feeling leaves (HTML, editors) have RN-specific views.
6. **MAUI** (optional but cheap): a `MauiViewRegistry.Register<XControl, XView>()` entry — see
   [MAUI Data Binding](/Doc/GUI/DataBindingMaui).
7. **Localization** of any user-visible strings per rule 4 — both catalogs, both homes.
8. **A gallery/doc page** under `Doc/GUI` with an executable `--render` cell, so the control is
   discoverable and its example is compiled and rendered by the doc gate on every PR.

For steps 4–5 the wire shape matters: the client sees your control as camelCase JSON with a
`$type` discriminator, patches arrive as RFC 6902 against the area document, and collection keys
arrive JSON-encoded. The protocol reference lives in the repo at
`clients/react/docs/live-protocol.md`.

## Testing across renderers

- **Server-side**: a layout-area render test (the `Tests` area pattern that `mw-plugin-test`
  executes, or an `AreaProbe`-based test) proves the control tree materializes — this covers every
  renderer's *input*.
- **JS shells**: the two parity ratchets prove coverage; component-level tests live next to the
  React controls (vitest). The shells' own suites (portal-next, RN) run in the `Clients` workflow
  on every PR.
- **What a green Blazor test does not prove**: any behaviour in rule 3's category. If the JS pack
  lacks the semantic, no existing test fails — which is exactly why the checklist puts the React
  renderer in the definition of done rather than in a follow-up.

Related: [UI Extensibility](/Doc/Architecture/UiExtensibility) ·
[Layout Areas](/Doc/GUI/LayoutAreas) · [GUI Data Binding](/Doc/GUI/DataBinding) ·
[React](/Doc/GUI/React) · [React Custom Controls](/Doc/GUI/ReactCustomControls) ·
[Localization](/Doc/Architecture/Localization)
