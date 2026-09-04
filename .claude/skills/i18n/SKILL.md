---
name: i18n
description: 'Localize every user-visible string as you write it — the portal ships English + German, so a hard-coded UI string is a bug that renders English for every viewer. Use whenever you write or review text a human reads on screen: buttons, labels, tooltips, aria-labels, placeholders, page titles, empty states, validation messages, toasts, dialog copy, menu entries, settings tabs, notifications, errors. Covers the two shapes (a Translation attribute on a declaration, a strings.en/de key everywhere else), the second key home in the plugins repo whose drift guard compares VALUES and merges core-first, the CurrentUICulture ban (it covers date and number FORMATTING too), and the list of things deliberately NOT translated.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /i18n — how does this render for a German viewer?

**Before you write ANY text a user will read, stop and ask that question.** This is not a
final-polish step or a follow-up ticket — it is part of writing the feature, the same way a null
check is. The portal ships English + German; **a hard-coded UI string is a bug**, because it renders
English for every viewer regardless of their language.

This applies to every surface, not just obvious ones: buttons, labels, tooltips, `aria-label`s,
placeholders, page titles, empty states, validation messages, toasts, dialog copy, menu entries,
settings tabs, notification text, and error strings. If a human reads it on screen, it needs a key
or a `[Translation]`.

> Canonical reference:
> [Localization.md](../../../src/MeshWeaver.Documentation/Data/Architecture/Localization.md)

## Prefer text that doesn't need translating at all

A language-neutral glyph beats a translated word: the AI menu's "new thread" entry uses **➕**, and
the node menu uses ✏️ 🔖 ➡️ 📋 🗑️. An icon plus a translated tooltip is usually better than a
translated label — it shrinks the translation surface and reads identically in every locale. (The
tooltip still needs a key.)

### 🚨 Inside authored content that preference is BINDING — clause 2

The rule was settled by
[#3203](https://github.com/Systemorph/MeshWeaver/issues/3203) and lives in
[ChromeAndContentLanguage.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ChromeAndContentLanguage.md):

> **Clause 1 — ownership decides the language.** Platform-/module-owned text (a catalog key, a
> module text table, a `[Translation]`) follows the **viewer**. Authored content renders **as
> authored**. A bare literal compiled into a view is **unowned — that is a bug**, not a third
> category.
>
> **Clause 2 — in-flow chrome minimises words.** A platform/module control rendered *inside* the
> author's flow carries **no translated visible label** where a glyph, a number or a symbol conveys
> the same thing. The localized text moves to the tooltip and the accessible name.

The symptom this exists for: a German-profile reader of an English lesson met **Ausführen** on the
code cell's Run button, mid-English-paragraph. The button already had a ▶ glyph and an already
localized `Title`; the word added nothing.

Two things this does NOT license:

- 🚨 **Dropping a visible label without an accessible name.** A tooltip *is* the control's
  accessible name — a glyph-only control keeps a localized `Title` / `aria-label`, or you have
  traded a language bug for an accessibility one.
- 🚨 **Making in-content chrome follow the CONTENT's language.** That is #3203's option (a), and it
  is declined: the Edu pack shipped it, measured it and reverted it (`EduTexts.ResolveChrome`'s
  comment: *"do not restore the old order"*), because its worst reader is an English speaker facing
  a German course whose every control is in a language they do not have.

Clause 2 reaches an **enumerated** list of in-flow surfaces — the code-cell toolbars, the fenced
block's copy affordance, the kernel placeholders inside the cell frame, `Edu/**` — not the shell. A
German node menu around an English document is not an inconsistency and keeps its words.

## Two shapes — pick by whether the text hangs off a declaration

- **On a declaration** (property label, node-type name, enum member) → add `[Translation("de", "…")]`
  next to the existing `[Description]`. Nothing else to wire; the translation rides beside the
  English so they cannot drift.
- **Everywhere else** (Blazor markup, inline `Controls.*`, toasts) → add a key to **both**
  `src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json` and read it via
  `Access.Localize("key")` (Blazor, `@inject AccessService Access`) or `host.Localize("key")`
  (layout areas).

```csharp
// Server-side layout area
Controls.Button(host.Localize("ui.createRelease"))
stack.WithView(Controls.Body(host.Localize("ui.noCodeDefined")))

// Blazor view
@inject AccessService Access
<button title="@Access.Localize("common.close")">…</button>

// On a declaration
[Description("Sync direction")]
[Translation("de", "Synchronisierungsrichtung")]
public SyncDirection Direction { get; init; }
```

`LocalizationTest` fails if a language is missing any English key, so a half-translated string cannot
merge — and it is now the ONLY localization guard in this repo.

## 🚨 A new key has a SECOND home — in another repo

The web clients' catalog lives at **`MeshWeaver.Plugins/clients/react/src/i18n/`**. `044a85618`
("The GUI leaves the platform", 2026-08-25) moved the web clients out; `clients/` in core now holds
only `python/` and `voice-gateway/`, and the six guards the old text and the `Clients` workflow
named — `i18n/localize.test.ts`, `render/parity.test.ts`, `ciTrigger.test.ts`,
`grpc-web/src/{wire,rest}Contract.test.ts`, `portal-next/e2e` — no longer exist **in this repo**. Do
not recreate them here; the mirror belongs where the client lives.

🚨 **The REQUIREMENT survived the move.** The guidance briefly read *"That is gone … do not go
looking for the mirror"*, which is true of the directory and false of the rule. On 2026-08-27 that
cost two keys: they were added server-side, the mirror was looked for **in core**, its absence was
read as deletion, and "there is no mirror" went into a merged PR body. The mirror was three keys
behind by then. **Deleted and relocated look identical from one repo** — assume relocated until you
have looked in the other one.

✅ **The drift guard DOES run — this paragraph used to say it did not, and that was the dangerous
half.** It read *"nothing currently runs those specs … the drift guard has been reporting to no one"*
(from MeshWeaver.Plugins#771). That lane now exists: the plugins repo's
**`RN app + web clients (typecheck + test)`** job runs `src/i18n/localize.test.ts`, and on
2026-08-28 it caught a real drift within minutes of the push — a `composition.column.provision`
value changed in the client catalog but not in core's:

```
FAIL src/i18n/localize.test.ts > catalog drift guard > strings.en.json is identical to the server catalog
AssertionError: value drift for "composition.column.provision": expected 'Install as' to be 'Provision as'
```

**Core is the source of truth, so the order is fixed**: the core catalog change merges FIRST, and
the plugins mirror PR stays red until it does. Do not "fix" that red by reverting the mirror — it is
the guard doing its job across a repo boundary.

🚨 **The guard compares VALUES, not just keys** — so it also catches the case `LocalizationTest`
cannot see: a key present in both catalogs whose text has diverged.

To run it by hand from the plugins checkout (it fails loudly, naming the paths it probed, when it
cannot find a core checkout — so a green there is real and a skip is impossible):

```bash
cd clients/react && MESHWEAVER_CORE=/path/to/MeshWeaver npx vitest run src/i18n/localize.test.ts
```

## 🚨 Never resolve from `CultureInfo.CurrentUICulture` / `CurrentCulture`

This covers *formatting* (dates, numbers, calendars), not just translated strings. Two independent
reasons:

1. A layout-area render hops the hub scheduler and an ambient AsyncLocal culture does not survive
   it, so one user's UI would pick up another user's language.
2. On Blazor Server the ambient culture is the **server process's** — i.e. the container the portal
   runs in — identical for every simultaneous viewer and unrelated to any of them.

`DateTimeView` defaulted its calendar culture that way until 2026-08-17 and rendered English month
names for German users regardless of their choice. Resolution is always explicit off
`AccessContext.Locale` (`AccessService.ViewerLocale()` / `host.ViewerLocale()`); derive a
`CultureInfo` from that when you need one.

## The language policy

**Take the language of the USER's computer, and put it on the user.** The onboarding form's first
field is a language picker pre-selected from the visitor's own `Accept-Language`; it writes
`User.Locale` at user creation. It is then editable in two places sharing ONE control
(`MeshNodeContentEditorControl` over `User.Locale`): the profile editor and User → Settings →
*Preferences*. A **silent** auto-detect of an unshipped language stores nothing (so a later
translation applies automatically); an **explicit** pick is always honoured — `Locales.TryMatch` vs
`Locales.Resolve` is that distinction in code.

## 🚨 Do NOT translate

- **LLM tool-parameter `[Description]`s** — model-facing; translating degrades tool-calling.
- **Wire identifiers** — `nodeType:Thread` in help text, `RequestAction("New")`, Fluent icon names.
- **The glossary terms kept English on purpose**: Thread, Mesh, Node, Agent, Skill, Harness,
  Provider, Namespace, Partition, Store.

## Adding a language is cheap by design

A tag in `Locales.Supported`, a `strings.{tag}.json`, and the matching `[Translation]` attributes.
Keep it that way: never scatter a second resolution mechanism, and never let a string bypass the
catalog "just this once".

## Checklist

- [ ] Every new user-visible string is a `[Translation]` on its declaration or a key in **both**
      `strings.en.json` and `strings.de.json`.
- [ ] Every string has an OWNER — platform catalog, module table, or the author. No bare literal
      compiled into a `.razor`/`.cs` view.
- [ ] A control rendered INSIDE authored content carries a glyph, not a translated word — and keeps
      a localized `Title`/`aria-label` as its accessible name.
- [ ] Nothing resolves culture from `CurrentUICulture`/`CurrentCulture` — including date/number
      formatting.
- [ ] If the string also appears in a web client, the mirror in
      `MeshWeaver.Plugins/clients/react/src/i18n/` was updated — and the core change merges first.
- [ ] No model-facing description, wire identifier, or glossary term was translated.
