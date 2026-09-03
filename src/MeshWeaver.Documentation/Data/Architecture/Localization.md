# Localization

The portal renders its chrome in the **viewer's** language. English and German ship today.

This is the direct twin of the per-viewer *timestamp* seam, because it is the same problem shape:
a display preference that must reach BOTH the Blazor circuit AND server-side hub layout areas that
have no browser.

```
User.TimeZoneId → AccessContext.TimeZoneId → AccessService.ToDisplayTime
User.Locale     → AccessContext.Locale     → AccessService.Localize
```

> 🚧 **Open question — chrome rendered INSIDE authored content.** "Chrome follows the viewer" is
> unambiguous for the node menu and settings. It is what puts **Ausführen** on the Run button of an
> English lesson, and #3203 asks whether that is right. A proposal — the ownership rule, why the
> content-language alternative was measured and reverted, and what a per-node language field would
> cost — is at [Chrome and content language](../ChromeAndContentLanguage). **It is not in force.** Until
> it is decided, this page describes what the platform does.

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

**The policy in one line: take the language of the user's own computer, and put it on the user.**
Never the *server's* culture — see the warning below.

1. **Chosen at onboarding, defaulted from the user's computer.** The onboarding form's first field
   is a language picker, pre-selected from the visitor's own computer language (the request's
   `Accept-Language`, already negotiated onto `AccessContext.Locale` by `UserContextMiddleware`).
   Changing it re-renders the form in the chosen language immediately, and submitting writes
   `User.Locale` in `UserOnboardingService.CreateUser`.

   This step exists because the auto-detector below **cannot** cover it: `BrowserPreferenceDetector`
   lives in the authenticated portal shell, so it does not run until *after* onboarding. Without a
   picker here, a German-speaking user filled in the form — and read the first screens — in English.
2. **Auto-detected once, afterwards.** `BrowserPreferenceDetector` reads `navigator.language`
   (alongside the IANA zone, in a single interop call) on first render and writes it **write-once** —
   a manual choice or an earlier session's value is never clobbered. This now mostly serves users
   who onboarded before the picker existed.
3. **Editable in two places, one control.** The **profile editor** (`{user}/EditProfile`) and the
   User → Settings → *Preferences* tab both carry a language picker. Both are the SAME
   `MeshNodeContentEditorControl` bound to the same `User.Locale` field on the node stream, so they
   cannot drift apart. The profile is where a user actually looks for "my language"; Preferences is
   where it sits next to the display time zone. Both store the BCP-47 tag (`de`) and show the
   endonym (*Deutsch*) — a German speaker looks for "Deutsch", not "German".
4. **Stamped onto the context.** `CircuitAccessHandler` resolves `User.Locale` once when the circuit
   context is built; it then rides `AccessContext.Locale` to every render path.

**Unsupported languages: silent guesses store nothing, explicit choices are honoured.** The
auto-detector (2) writes *nothing* for a language this deployment does not ship, leaving the profile
empty so a translation shipped later applies automatically instead of pinning the user to a tag we
would only ever render in English. The onboarding picker (1) always stores its selection, including
`en` when left at the default — because that value was on screen, labelled, and submitted. The
distinction is *silent guess* vs *seen and accepted*, and `Locales.TryMatch` (nullable) vs
`Locales.Resolve` (always a tag) is how it is spelled in code.

> 🚨 **"The computer's language" means the USER's computer, never the server's.**
> `CultureInfo.CurrentCulture` / `CurrentUICulture` on Blazor Server is the *server process*
> culture — the machine the portal happens to run on (an `en-US` container, in practice), identical
> for every simultaneous viewer and unrelated to any of them. `DateTimeView` defaulted its calendar
> culture to it until 2026-08-17, so month names and date order rendered English for a German user
> no matter what they had chosen. It now resolves `AccessService.ViewerLocale()`. If you need a
> `CultureInfo` for formatting, derive it from the viewer's locale — never from ambient culture,
> which would not survive a hub-scheduler hop anyway.

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
🚨 **The guard that pinned this is GONE.** `AnonymousCircuitLocaleSeedTest` ran its cases over **both**
transports, and the long-polling rows are what caught the defect above. It lived in core at
`test/MeshWeaver.Hosting.Blazor.Test/`; when `MeshWeaver.Hosting.Blazor` moved to MeshWeaver.Plugins
its sibling tests went with it and **this file did not**. It exists on neither repo's `main` today, so
the anonymous seed currently has **zero** coverage — `Locales.Negotiate` is still tested, but nothing
tests whether the negotiated value reaches a circuit. Tracked as
[MeshWeaver.Plugins#1273](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1273).

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

- **Owner-side diagnostics** — the `Describe*` helper family and the strings they feed into
  `ILogger` calls, exception messages, and error payloads carried on the wire. See the boundary
  below; this is the entry reviewers most often challenge.

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

### 🚨 The boundary: a viewer's message vs. an owner's diagnostic

"Errors are localized" is true of the errors a **viewer reads**, and reviewers reasonably read the
rule that way. It is not true of the diagnostic layer underneath, and the distinction is not
squeamishness about effort — localizing there makes the product *worse*.

| | viewer message | owner diagnostic |
|---|---|---|
| reaches a human via | a control: label, toast, dialog, validation, empty state | `ILogger`, an exception message, an error payload on the wire |
| written for | the person who tried to do the thing | whoever is debugging the mesh |
| vocabulary | the user's domain | partitions, paths, stream ids, providers, node types |
| **localize?** | **yes, always** | **no** |

The `Describe*` family is the canonical diagnostic shape — `MessageSizeGuard.Describe` /
`DescribeGrainDispatch` / `DescribeRouterDispatch`, `CancellationClassifier.Describe`,
`QueryIdentity.DescribeUnresolved`, `StoreReachability.DescribeNotAttempted` /
`DescribeMayHavePartiallyLanded`, `RequiredModuleStatus.Describe`,
`ModuleActivationStatus.DescribeUnresolvable`. None is localized, and
`MeshWeaver.Mesh.Contract` — which holds several of them — contains six `Localize(` calls in total,
none on a `Describe*`.

**Three reasons this is a decision and not a backlog item:**

1. **A translated fragment in an English sentence is worse than English.** These strings are
   composed into carriers that are themselves literals — `MeshNodeStreamExtensions` builds
   `$"Update of '{path}' failed: {errorType}"` and falls back to the bare
   `"Update rejected by owner"`. Localizing the inner clause yields a German phrase spliced into an
   English frame: harder to read for the German viewer *and* harder to grep for the engineer.
2. **The content is operator vocabulary in every language.** A message naming a namespace, a mesh
   path, a stream id and a store provider does not become more comprehensible in German. What makes
   it comprehensible is knowing the platform.
3. **Grep-ability is a property of the diagnostic layer.** A log line or exception you cannot search
   for by its English text — because it may have been emitted in any of N languages — is materially
   harder to trace, and these are the strings that get pasted into issues.

**If a diagnostic really is surfacing to a viewer, that is a bug at the SURFACE, not here.** The fix
is for that surface to map an error code to a localized message, never to print an owner-side
sentence. `MeshNodeErrorCode` exists precisely so a UI can do that without parsing prose. Localizing
the diagnostic would hide the defect behind a translated version of a string the viewer should never
have been shown.

**Reviewing this:** an automated reviewer flagging a `Describe*` helper or an `Error` payload as an
unlocalized user-visible string is applying the right rule at the wrong layer. Point it at this
section. The rule genuinely bites the moment the string reaches a `Controls.*` literal, an
`aria-label`, or anything a `LayoutArea` renders.

## Tests

- `LocalizationTest` (MeshWeaver.Messaging.Hub.Test) — catalog loads, fallback chain, plurals,
  attribute lookup, `Locales.Negotiate` over real `Accept-Language` shapes, and **every shipped
  language covers the full English key list with no orphans**.
- `LocalePreferenceTest` (MeshWeaver.Hosting.Monolith.Test) — the write-once decision.
- `AnonymousCircuitLocaleSeedTest` (MeshWeaver.Hosting.Blazor.Test) — the anonymous seed, driven over
  a **real SignalR WebSocket into Blazor's real `ComponentHub`**, because the only question that
  matters is whether the circuit can still see the request that established it. A unit test of the
  negotiation would stay green while the browser saw nothing.
