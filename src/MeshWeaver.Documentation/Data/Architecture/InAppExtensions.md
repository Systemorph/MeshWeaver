---
Name: In-App Extensions
Category: Architecture
Description: Packages that extend an APP rather than standing beside it — the model providers and coding harnesses of the Threads app, the signing authority of the person app. How a package declares its host (hostedIn, extensionSlot), how the host offers it (the Store's Extensions shelf, where Get or a purchase is the in-app purchase), and how an acquired extension surfaces inside the host (a PersonApp contribution gated on RequireAddressAccess).
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="3"/><path d="M12 8v8M8 12h8"/></svg>
---

# In-App Extensions

**An in-app extension is a Store package that extends an APP instead of being an app of its own** —
the way an in-app purchase extends a phone app. A person never meets it as a tile on their home: they
meet it inside the app it belongs to, get it (or buy it) there, and from then on its settings are part
of that app.

| Host app | Where | Its extensions |
|---|---|---|
| **Threads** | `AI/AiThreads` | the coding-assistant **harnesses** (Claude Code, Copilot, Codex, …) and the **model providers** (Anthropic, OpenAI, Azure AI Foundry, …) |
| **Person app** | `/{user}/Settings` ([Settings by Owner](../AdminApp)) | the **signing authority** — Electronic Signature, with Swisscom AIS, Skribble and DeepSign |

Why not apps of their own: a harness means nothing outside a thread, and a signature provider means
nothing outside the person who signs. As separate apps they were tiles on every home that each opened
a page asking you to go somewhere else first.

## The three parts

### 1. The extension declares its host — `hostedIn` + `extensionSlot`

On the package root (`PluginContent`, owned by the Store):

```json
"hostedIn": "{user}/Settings",
"extensionSlot": "signingAuthority"
```

- **`hostedIn`** — the host app's node path (`AI/AiThreads`), or `{user}/Settings` for the person app,
  which is a different node for every person. A hosted package gets **no home tile**, a tile minted
  earlier is purged automatically, and the Store catalog lists it only to a global admin (who installs
  and updates packages for the instance).
- **`extensionSlot`** — optional: which section of the host lists it (`harness`, `modelProvider`,
  `signingAuthority`). A host with one kind of extension ignores it.

Nothing else changes about the package: it keeps its entitlement, its gating, its install plan, its
cover at its own URL, and its own NodeTypes.

### 2. The host offers it — the Store's Extensions shelf

The host embeds ONE shared control, served by the Store:

```csharp
Controls.LayoutArea(new Address("Store"), new LayoutAreaReference("Extensions") { Id = "AI/AiThreads#modelProvider" })
```

The shelf lists every package whose `hostedIn` is the host (and, after `#`, whose `extensionSlot` is
the slot). Each entry is the package's card plus the package's **own** `CoverCta` — Get, Buy (a
Stripe order), Update or Uninstall, decided on the package's hub from the viewer's entitlement and
install state. **That button is the in-app purchase**: the host never learns what a purchase is, and
the Store's one acquisition chain (entitle → stamp → install) runs unchanged.

The person app's shelf is itself a person-app tab — **Extensions** — contributed by the Store as data
(below), so a mesh without the Store simply has no Extensions tab.

### 3. The acquired extension surfaces in the host

For the **person app** — compiled in core, so a node-native package cannot register a tab on it — the
surface is a `UiContribution` with **`Context: PersonApp`**:

```json
{
  "$type": "UiContribution",
  "context": "PersonApp",
  "address": "Signature/Workspace",
  "area": "SigningAuthority",
  "label": "Signing authority",
  "labelKey": "personApp.signingAuthority",
  "order": 80,
  "requiredPermission": "Update",
  "gates": { "requireAddressAccess": true }
}
```

- The tab shows **only on the viewer's own user root** (`PersonApp.IsPersonAppHub`) — never on a
  Space's, a node's or another person's settings page.
- Its body embeds `area` of `address` — the extension's OWN hub, rendering in the viewer's context
  with its own access checks. `address` must lie inside the contribution's own partition, exactly like
  a `Profile` section; anything else is dropped.
- **`requireAddressAccess`** is the "has the viewer acquired it" gate: a Store package is gated, so an
  un-entitled viewer cannot read below its root, and "may read the embedded address" IS "holds the
  extension". It follows the viewer's LIVE effective permissions: Get shows the tab without a reload, a
  revoked grant hides it, and a pending or undetermined verdict hides it (missing evidence ⇒ do not
  show). The probe is `CheckPermissionOutcome`, so an undetermined verdict is logged as a degraded
  dependency rather than read as "not held".
- A contributed tab **never shadows a tab already on the page**: one whose id equals a built-in
  person-app tab (`Sharing`, `Preferences`, …) is dropped, and seed validation reports it.

For a **compiled host** (Threads, in `MeshWeaver.AI`) the host renders its extensions' settings
itself, as it always could — the harness option links to `{user}/Harness/{id}`; a model provider opens
its own entry point.

## The rules

- **An extension is a PACKAGE, never a second copy of the host.** It does not fork the host's page; it
  contributes to it through the shelf and (for the person app) a `PersonApp` tab.
- **The purchase is the package's own `CoverCta`.** No host re-implements Get, checkout or install.
- **The tab is gated on holding the extension, not on its existence.** Without `requireAddressAccess`
  an un-entitled viewer would get a tab whose body is an access-denied page.
- **Settings the person owns live under the person** — `{user}/_Signature/…` for signing credentials —
  never in the package partition.

## Where it is implemented

| Part | Code |
|---|---|
| `PersonApp` context, `RequireAddressAccess` gate | `UiContribution` / `UiContributionGates` (`MeshWeaver.Graph`), `UiContributionProjection.ProjectPersonAppTabs`, `SettingsMenuItemsExtensions.ContributedPersonAppTabs` / `ApplyAddressAccess`; tests `PersonAppContributionTest` |
| `hostedIn`, `extensionSlot`, the Extensions shelf, the person app's Extensions tab | Store package (`Store/Core/Source/PluginContent.cs`, `Store/Catalog/Source/ExtensionShelf.cs`, `Store/PersonAppTabs/Extensions`) — MeshWeaver.Plugins |
| Threads app sections | `AiThreadsApplication` (MeshWeaver.Plugins `src/MeshWeaver.AI`) |
| Signing authority | Signature package (`Signature/PersonAppTabs/SigningAuthority`, `SigningAuthority` area of `Signature/Workspace`) — MeshWeaver.Plugins |
