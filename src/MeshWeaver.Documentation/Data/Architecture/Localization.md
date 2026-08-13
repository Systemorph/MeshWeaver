# Localization

The portal renders its chrome in the **viewer's** language. English and German ship today.

This is the direct twin of the per-viewer *timestamp* seam, because it is the same problem shape:
a display preference that must reach BOTH the Blazor circuit AND server-side hub layout areas that
have no browser.

```
User.TimeZoneId → AccessContext.TimeZoneId → AccessService.ToDisplayTime
User.Locale     → AccessContext.Locale     → AccessService.Localize
```

## The one rule: resolve explicitly, never from ambient culture

`CultureInfo.CurrentUICulture` is **not** used and must not be introduced. A layout-area render hops
the hub's scheduler; an `AsyncLocal` ambient culture does not reliably survive that hop, so an
ambient design would silently render one user's UI in another user's language.

Instead, `LayoutAreaHost` captures the subscriber's `AccessContext` at construction and restores it
for the render scope — which is exactly what makes an explicit read correct.

## Two lookup shapes, one resolution rule

Both resolve the viewer's language through `Locales.Resolve`, so they can
never disagree.

### 1. `[Translation]` — for text attached to a declaration

Property labels, node-type names, enum members, class descriptions. English stays where it already
is; the translation rides next to it, so the two cannot drift apart the way a key-indirected
resource table allows.

```csharp
[Description("Display time zone (IANA)")]
[Translation("de", "Anzeige-Zeitzone (IANA)")]
public string? TimeZoneId { get; init; }
```

`[Description]` is read as a UI label in only three places, so wiring those localized **every
generated form label at once**:

| Site | What it feeds |
|---|---|
| `EditorExtensions.cs` → `MapToControl` | the `Edit` macro's property skins |
| `EditorExtensions.cs` → `GetToggleableDisplayName` | click-to-edit property views |
| `MeshNodeContentEditorControl.FromType` | node-bound content editors |

> 🚨 Do **not** put `[Translation]` on the `[Description]` attributes that describe **LLM tool
> parameters** (`MeshPlugin`, `McpMeshPlugin`, `Plugins/*`). Those are model-facing, not user-facing;
> translating them degrades tool-calling.

### 2. The string catalog — for text with no declaration

Blazor markup, inline `Controls.*` literals, toasts, dialog copy. Keys are dotted and namespaced by
UI area (`chat.new`, `menu.edit`, `settings.privacy`); **`strings.en.json` is the key list of
record.**

```csharp
// Blazor component
@inject AccessService Access
<button title="@Access.Localize("common.close")">…</button>

// Server-side layout area
Controls.Button(host.Localize("ui.createRelease"))

// Plural
Access.LocalizePlural("plural.message", count)   // "3 messages" / "3 Nachrichten"
```

Pure builder helpers that deliberately take **no** host (documented as unit-testable without a
layout host) take the viewer's language as an explicit input instead — which keeps them pure and
makes their German output testable too:

```csharp
public static StackControl BuildLog(ActivityLog log, string? locale = null)
    => …Controls.Label(LocalizationCatalog.Get("ui.running", locale))…

// caller
BuildLog(log, locale: host.ViewerLocale())
```

## Language resolution

`Locales.Resolve` falls back in three steps: exact match → primary subtag → English. So `de-CH`,
`de-AT` and `de_DE.UTF-8` all serve German, and anything unshipped renders English rather than
blank.

Lookup never throws and never returns null. A key missing from the requested language falls back to
English; a key missing from English falls back to **the key itself**, so an untranslated string
surfaces as a visible `chat.new`-shaped token — loud enough to notice in review, harmless enough to
ship.

## Where the viewer's language comes from

1. **Auto-detected once.** `BrowserPreferenceDetector` reads `navigator.language` (alongside the
   IANA zone, in a single interop call) on first render and writes it **write-once** — a manual
   choice or an earlier session's value is never clobbered.
2. **Overridable.** The User → Settings → *Preferences* tab has a language picker. It stores the
   BCP-47 tag (`de`) but shows the endonym (*Deutsch*) — a German speaker looks for "Deutsch", not
   "German".
3. **Stamped onto the context.** `CircuitAccessHandler` resolves `User.Locale` once when the circuit
   context is built; it then rides `AccessContext.Locale` to every render path.

An **unsupported** browser language deliberately writes *nothing*, leaving the profile empty — so a
translation shipped later applies automatically instead of the user being pinned to a tag we would
only ever render in English.

### …and where an ANONYMOUS visitor's comes from

Steps 1–3 all read a **profile**, and an anonymous visitor does not have one. Without a fourth
source, `AccessContext.Locale` is null for every logged-out visitor and the whole feature is inert
for exactly the audience it matters most to: the first-time viewer of a paywall, an invitation link
or a public course page is anonymous *by definition*.

So the request's `Accept-Language` header is negotiated against `Locales.Supported` and **seeded**
onto the identity, on both entry paths:

| Path | Where | Reads the header from |
|---|---|---|
| SSR / HTTP request | `UserContextMiddleware` | `HttpContext.Request.Headers.AcceptLanguage` |
| Blazor circuit | `CircuitAccessHandler` **constructor** | `CircuitRequestLanguage`, published per hub invocation by the global `CircuitRequestLanguageFilter` |

🚨 **The circuit reads it off the SignalR CONNECTION, not off `IHttpContextAccessor`.** The accessor
works over WebSockets — the upgrade request stays in flight for the connection's life — and returns
nothing under **long polling**, where every poll is a separate request that ASP.NET disposes (which
nulls the accessor's holder for every flow that captured it). A browser behind a proxy that blocks
WebSockets falls back to long polling, i.e. exactly a corporate network, so an accessor-only fix
would reach most visitors and silently miss the rest. SignalR keeps an `IHttpContextFeature` on the
connection and refreshes it per request, so `HubCallerContext.GetHttpContext()` answers for every
transport; the filter reads it in the hub invocation that *creates* the circuit handlers. The
accessor remains only as a fallback for a host that runs the Blazor hub without the filter.
`AnonymousCircuitLocaleSeedTest` runs its cases over **both** transports — the long-polling rows are
what caught this, and are what stop it regressing.

`Locales.Negotiate` does the parsing: the full RFC 9110 list with `q=` weights, tried in descending
weight, each matched by `Locales.TryMatch` so region variants fold onto the primary subtag exactly as
everywhere else (`en-GB` → `en`). `q=0` is an explicit refusal and `*` is an absence of preference —
both are skipped, so neither can pin a caller to a language it never asked for. Nothing matched
returns **null**, not `en`, so "unsupported" stays distinguishable from "asked for English".

Two properties this rests on, both load-bearing:

- **It is a SEED, never an override.** A signed-in user's stored preference still wins:
  `MeshUserProjection.Apply` — the single projection both entry paths use — keeps a profile's
  language when the profile states one and only falls back to the seed when it does not. (Before
  this, the two paths projected differently: the circuit read the profile's locale and time zone, the
  middleware read only id and name. That divergence was invisible while nothing seeded a locale and
  would have rendered German SSR chrome for an English-profile user the moment one existed.)
- **It lands BEFORE the first render.** The header is read in the circuit handler's *constructor*,
  because Blazor resolves the circuit's `CircuitHandler`s — and then runs `OnCircuitOpenedAsync` /
  `OnConnectionUpAsync` — before it adds and renders any root component. That ordering is what makes
  the seed effective at all: `LayoutAreaHost` captures the access context in its **constructor**, so
  a locale arriving mid-circuit re-renders nothing.

Note this is still an *explicit* resolution off `AccessContext.Locale` — the header is read once, at
identity time, and never becomes a second ambient mechanism. `CultureInfo.CurrentUICulture` is not
consulted anywhere (see "The one rule" above).

## Adding a language

1. Add the tag to `Locales.Supported` and an endonym to `Locales.DisplayNames`.
2. Add `Localization/strings.{tag}.json` as an `EmbeddedResource` **with `WithCulture="false"`**.
3. Add `[Translation("{tag}", …)]` next to the UI-facing `[Description]` attributes.

> 🚨 `WithCulture="false"` is load-bearing, not boilerplate. The SDK infers a culture from the
> `.en.`/`.de.` segment of an `EmbeddedResource` filename and, having inferred one, routes the file
> into a **satellite assembly** instead of the main one. The result is silent: the build succeeds,
> the assembly carries zero manifest resources, every lookup falls through to the key-fallback path,
> and the UI renders raw `chat.new` tokens. `LocalizationTest.EnglishCatalog_IsLoaded` is the guard.

## What stays English on purpose

- **Wire identifiers** — `nodeType:Thread` in help text, `RequestAction("New")` action keys, Fluent
  icon names. Translating these breaks the app.
- **LLM tool-parameter descriptions** — model-facing (see above).
- **Code examples in documentation** and XML doc comments.
- **Sample/demo app content** (Northwind, Cornerstone, PensionFund) — example business domains with
  their own vocabulary.
- **Product and technical vocabulary**, by glossary decision: *Thread, Mesh, Node, Agent, Skill,
  Harness, Provider, Namespace, Partition, Store, Token, Chat, Layout Area*. These are explained in
  the course primer rather than translated. Note `Store` means "app store" — never translate it as
  the verb *speichern*.

## Tests

- `LocalizationTest` (MeshWeaver.Messaging.Hub.Test) — catalog loads, fallback chain, plurals,
  attribute lookup, `Locales.Negotiate` over real `Accept-Language` shapes, and **every shipped
  language covers the full English key list with no orphans**.
- `LocalePreferenceTest` (MeshWeaver.Hosting.Monolith.Test) — the write-once decision.
- `AnonymousCircuitLocaleSeedTest` (MeshWeaver.Hosting.Blazor.Test) — the anonymous seed, driven over
  a **real SignalR WebSocket into Blazor's real `ComponentHub`**, because the only question that
  matters is whether the circuit can still see the request that established it. A unit test of the
  negotiation would stay green while the browser saw nothing.
