---
nodeType: Skill
name: /ui-extensibility
description: Extend MeshWeaver UI the right way — pick the lane (in-mesh layout-area source, compiled view pack, or core), register through the real seams, and make every addition render on Blazor, Next.js, and React Native alike.
icon: Layout
category: Skills
order: 16
---

Extending the UI never means "add a page to the portal". It means picking one of three lanes and
registering through an existing seam. Read [UI Extensibility](/Doc/Architecture/UiExtensibility)
for the architecture and [Writing UI for Every Renderer](/Doc/GUI/CrossRendererAuthoring) for the
cross-client rules before writing anything.

# 1. Pick the lane — the decision tree

1. **Can the feature be expressed as controls?** (layout areas, dialogs, menus, forms, grids —
   no JS interop, no third-party component library, no root-DI service, no hosted service)
   → **Lane 1: in-mesh layout-area source in a content plugin.** The default; installable and
   removable without a deploy; renders on every client for free. Follow
   [/layout-area](/Skill/layout-area). Remember the in-mesh limits — public framework surface
   only, per-hub configuration only.
2. **Does it need JS interop or a third-party component library?**
   → **Lane 2: a view pack** — a plain class library with component types plus one registration
   entry point. No routable pages, no App.razor tags, no router work.
3. **Is it a generic renderer of a framework control that every deployment wants?**
   → **Lane 3: core.** This is rare. The portal gained a hard-wired feature registration every
   ~3 days for a month; one built-in was retired into its plugin nine days after being added.
   Default to lanes 1–2.

# 2. The view-pack recipe (lane 2)

Model: `MeshWeaver.Blazor.Radzen`.

- One hub entry point: `AddXxxViews(this MessageHubConfiguration c)` doing
  `c.WithType(typeof(XControl)).AddViews(l => l.WithView<XControl, XView>())` per control.
- **Register BEFORE `AddBlazor()`** — view maps are first-match-wins and the core default mapping
  is terminal; a pack registered after it is silently dead.
- **Assets are self-loaded, never shell tags**: per-component stylesheets via `<HeadContent>`;
  classic third-party scripts via the memoized `_content/MeshWeaver.Blazor/assetLoader.js` in the
  pack's view base class, gating the components on an `AssetsReady` flag.
- **Never ship copies of contract assemblies** — compile against the host's
  `MeshWeaver.Layout`/`MeshWeaver.Blazor`; a same-named second copy is the silent-null trap-door.
- Root DI is only for services components `[Inject]` and hosted services; everything else rides
  hub configuration.

# 3. New control — definition of done

A control is done when it renders on EVERY client, not when the Blazor view works:

1. Immutable control record (geometry/derivation computed server-side ON the record — the
   `TowerControl.Layout()` pattern), plus `WithType(typeof(XControl))` registration.
2. Blazor view via the pack seam.
3. React renderer in `clients/react/src/controls/` — the Blazor-parity ratchet
   (`clients/react/src/render/parity.test.ts`) turns the Clients workflow red until it exists;
   never alias or placeholder it.
4. React Native `rnPack` entry (`clients/react-native/src/parity.test.ts` is its ratchet).
5. Optional MAUI `MauiViewRegistry.Register` entry.
6. Every user-visible string: `host.Localize` key in BOTH
   `src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json` AND mirrored into
   `clients/react/src/i18n/` (the drift guard fails otherwise).
7. A `Doc/GUI` gallery page with an executable `--render` cell — the doc gate compiles and
   renders it on every PR.

# 4. Cross-renderer rules (the ones that bite)

- Controls only — hand-built HTML renders as an opaque blob on JS shells.
- The portable interaction surface is click / blur / close-dialog / bound-field edits. More than
  that is a framework feature, not an area feature.
- Semantic parity is NOT guaranteed by the type ratchets: verify a control's write-path semantics
  exist in the JS pack before depending on them (the `AutoSaveAddress` lesson — edits silently
  didn't persist on JS shells).
- Menus are `$Menu:*` areas in the stream — chrome expressed as controls needs no parity work.

# 5. Rollout

Merged is not shipped. Lane 1 reaches instances through the plugin registry
([Plugin Registry](/Doc/Architecture/PluginRegistry),
[Plugin Update On Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild)); a plugin in
`PluginCatalog:InstallByDefault` installs unattended on a NEW instance's first boot. Lane 2 rides
the image and activates via platform configuration
([Feature Flags](/Doc/Architecture/FeatureFlags)). Standing up a new instance end to end is
[Deployment](/Doc/Architecture/Deployment) — wire plugin grants and the install-by-default list as
part of provisioning, or the instance boots bare.
