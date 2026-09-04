---
Name: Chrome and content language
Category: Architecture
Description: Whose language a string renders in when the application speaks inside a document the author wrote — the ownership rule, why the content-language alternative was already tried and reverted, and what a per-node language field would cost.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 8 6 6"/><path d="m4 14 6-6 2-3"/><path d="M2 5h12"/><path d="M7 2h1"/><path d="m22 22-5-10-5 10"/><path d="M14 18h6"/></svg>
---

# Chrome and content language

> **Status: IN FORCE.** Adopted 2026-09-04 in answer to
> [#3203](https://github.com/Systemorph/MeshWeaver/issues/3203): the maintainer accepted the two
> clauses below and declined the issue's option (a). Chrome keeps following the viewer.
> [Localization](../Localization) carries the clauses as the rule a contributor is held to; this page
> carries the measurement behind them — why the content-language alternative was declined, what a
> per-node language field would cost, and which code the clauses still reach. Adoption status per
> item is in **Adoption** below.

## The observation

A German-profile reader opened an English course page. The lesson text was English. The code cell's
button read **Ausführen**. The node menu read *Bearbeiten · Anheften · Verschieben · Löschen*. The
quiz beneath the lesson read *Question 6 of 6*.

The reader's reaction was *"language should be applied as consistently as possible."*

Every one of those is correct in isolation. The button and the menu follow the viewer, which is the
platform's documented rule. The lesson is English because its author wrote English. The quiz is
English because nobody localized it. The rule is consistent per component and incoherent per page,
and the page is what a reader sees.

## The rule

**Every user-visible string has exactly one owner, and the owner decides the language.**

| The string is owned by | It renders in | Because |
|---|---|---|
| the **platform or a module** — it lives in a string catalog or a module's text table | the **viewer's** language | it is the application talking to the person operating it |
| the **author** — it comes out of a node's content: the body, `Name`, `Description`, a typed field an author filled in | **the language it was authored in**, untouched | it is the author talking to a reader |
| **nobody** — a bare literal compiled into a view | this is a **bug** | a string with no owner has no language rule, so it renders whatever the developer typed |

That is the whole language decision, and it is already what the platform does. What #3203 exposes is
not a wrong answer to it; it is two *other* defects that look like a language problem:

1. **Unowned strings.** The quiz's *"Question 6 of 6"* and *"This is a copy in your own space…"* went
   through no catalog and no text table. They are unowned, so they render English for everybody — a
   German reader gets a German frame, German headings, and then an English progress counter. Giving
   them an owner is the fix, and the owner is the module, so they become *Frage 6 von 6*.
2. **Owned strings in the wrong form.** `common.run` renders the word **Run**/**Ausführen** on a
   button that *already* carries a ▶ glyph and an *already localized* tooltip. The word adds no
   information and puts a German token in the middle of an English paragraph. Nothing about the
   ownership is wrong; the *form* is.

So the two clauses:

> **Clause 1 — ownership decides the language.** Platform- and module-owned text follows the viewer.
> Authored content is rendered as authored. An unowned literal is a defect, not a third category.
>
> **Clause 2 — in-flow chrome minimises words.** A platform- or module-owned control rendered
> *inside* the author's flow must carry no translated visible label where a glyph, a number or a
> symbol conveys the same thing. The localized text moves to the tooltip and the accessible name,
> which is where a reader who needs it will look and where it cannot land in the middle of a
> sentence.

Clause 2 is not new doctrine. [User Interface](../UserInterface) already says *"a language-neutral
glyph plus a translated tooltip beats a translated label"*. Clause 2 makes it **binding** for the
in-flow case rather than preferred, because that is the only case where ignoring it produces the
artefact #3203 reports.

## Why the obvious alternative is not recommended

#3203 offers, as option (a), that in-content controls follow the **content's** declared language and
fall back to the viewer only when the content declares none.

**That is the rule the Edu pack shipped, measured, and deliberately reverted.**
`Edu/Module/Source/EduTexts.cs` in MeshWeaver.Plugins used to resolve
`Primary(courseLanguage) ?? Primary(viewerLocale) ?? "en"` — the course first. Its own comment
records what that cost:

> 🚨 **The order is the fix, and the parameter order states it.** … Since `Primary("de-CH")` is
> `"de"`, the `??` chain short-circuited and **the viewer was never consulted at all**:
> AgenticPrimerDe served "Übungen" / "Weiter" to every learner on earth, whatever language they read
> in.

and then, explicitly:

> 🚨 **Do not restore the old order.** The argument it was written for — "German lessons framed by
> English headings read as broken" — was answered by drawing the chrome/content line instead … A
> learner who reads English gets an English frame around German prose, which is what they asked for;
> the alternative **imposed German buttons on a reader who cannot read them**.

There is a second measurement on the same shape: Store's twin,
[#1349](https://github.com/Systemorph/MeshWeaver/issues/1349), where content-language chrome cost a
day of e2e runs — the harness pinned the English label, nothing ever matched, and the retry loop
timed out presenting as *"the paywall never came up"*.

Option (a) is symmetrical-looking and asymmetrical in effect. It reads well for the case that
prompted #3203 — a German reader of an English course, who can plainly read English. It reads
catastrophically for the case that prompted the revert — an English reader of a German course, who
gets a page whose every control is in a language they do not have. **A rule must be judged on the
reader it serves worst, and option (a)'s worst reader cannot use the page at all.**

## The mechanical boundary

The brief for this rule was that the boundary be decided mechanically, not string by string.

**Start from what is NOT available.** There is no runtime chrome-vs-content boundary in this codebase,
and it is not a small gap:

- `UiControl` carries `Id`, `Style`, `Readonly`, `Skins`, `Class`, `IsUpToDate` and click plumbing —
  **no chrome/content discriminator**. There is no `IChromeControl`/`IContentControl`, no `[Chrome]`
  attribute, and every control lives in the one `MeshWeaver.Layout` namespace.
- `RenderingContext` carries `Area`, `Layout`, `DataContext`, `DisplayName`, `Parent`, `Depth` —
  `Parent`/`Depth` exist as a recursion guard. Nothing says "authored content".
- The Blazor cascading parameters (`Context`, `ThemeMode`, `DataContext`, `Model`; and
  `LayoutAreaView`'s `Top`/`Fill`/`DrivesMenu`) are layout and data flags, deliberately decoupled from
  embedding semantics.
- **Nothing survives the markdown → embedded-area hop** except an address, an area name, an area id, a
  spinner style and — on *one* of the two embed paths — `showHeader=false`. The embedded area is a new
  server subscription on another hub; it does not know a document embedded it. `showHeader` is not a
  substitute: it is absent from ```` ```layout ```` fences and from every kernel result area, an author
  can flip it with `?showHeader=true`, and it means "hide this node's own header", not "you are inside
  someone's prose".
- `<article class="markdown-body">` exists in the **DOM**, and is invisible to the component tree.
  `MarkdownCodeCellToolbar` is hydrated out of the host document's HTML, injects `AccessService`
  *itself*, and has no parameter, cascade or ancestor that could tell it otherwise.

So a position-based rule would have to be *built* first. That matters for the recommendation, because:

> **~722 of the 1104 catalog keys are reachable from a control that can appear inside authored
> content**, against roughly 113 that are shell-only (login, onboarding, nav, side panel, portal
> header); the remaining ~268 have no literal C#/Razor/TSX call site to classify from — mostly React
> `t()` with computed keys. A MeshWeaver layout area is addressable and therefore embeddable, so
> `MeshWeaver.Graph.Views` and `MeshWeaver.Graph` — node pages, code cells, activity views, catalogs —
> are the two largest call-site blocks and are all embeddable.
>
> *(Counted by matching `Localize` / `LocalizationCatalog.Get` / `localize` / `t(` call sites with a
> literal key across both repos' non-test sources, then classifying a key as shell-only when **all** its
> call sites live in a shell project path. It is an estimate; the order of magnitude is the point.)*

A rule that says "in-content controls follow the content" therefore does not resolve ~5% of the
catalog. It opens a key-by-key or control-by-control decision over **most of it**, with nothing in the
code able to record the answer. That is the practical reason option (a) is not merely risky but
unbuildable at its stated scope.

**The classifier that IS mechanical is the string's storage location, not its position on screen** —
because storage is decided at authoring time, in the source tree, where a developer and a reviewer can
both see it, and it needs no runtime signal at all.

| Where the string lives | Owner | Language |
|---|---|---|
| `src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json` | platform | viewer |
| a module's own text table (`EduTexts`, `CourseInviteTexts`) | module | viewer |
| a `[Translation]` beside a `[Description]` on a declaration | platform/module | viewer |
| `MeshNode.Name` / `.Description` / `.Category`, or overrides in `ILocalizedNodeText.Translations` | author | as authored |
| the node's body, or any typed content field an author edits | author | as authored |
| a bare literal in a `.razor` / `.cs` view | **nobody — fix it** | — |

There is no judgement call in that table and no per-string debate: a developer writing a string
chooses where to put it, and that choice *is* the language decision. The one thing they may not do
is put it nowhere.

**Position enters only through clause 2**, and there it is an ENUMERATED list, not an inferred
property. Clause 2 does not need the runtime boundary that does not exist, because it applies to a set
small enough to name — the controls the markdown pipeline hydrates directly into a document body:

| In-flow surface | Where |
|---|---|
| the code-cell toolbar (Blazor) | `MeshWeaver.Plugins/src/MeshWeaver.Blazor/Components/MarkdownCodeCellToolbar.razor` |
| the fenced code block's copy affordance | `MeshWeaver.Plugins/src/MeshWeaver.Blazor/Components/CodeBlock.razor` |
| the code-cell toolbar on a Code **node** page | `MeshWeaver.Plugins/src/MeshWeaver.Graph.Views/CodeViews.cs` |
| the kernel placeholders rendered inside the cell frame | `src/MeshWeaver.Markdown/MarkdownViewLogic.cs` **(this repo)** |
| the Edu lesson frame, exercise grid and quiz | `MeshWeaver.Plugins/Edu/**` |

Naming the set is the point. A guard over "every control everywhere" is a guard nobody keeps green;
a guard over five files is one a reviewer can extend deliberately when a sixth in-flow control is
built. **The list growing is the process working**, and the list is short because embedding a whole
layout area into prose is rare compared to reading a page.

Everything else — the node menu, navigation, settings, toasts, dialogs, the composer — is shell, and
clause 2 does not reach it. A German node menu around an English document is the same arrangement as
a German application menu around an English PDF, and nobody reads that as an inconsistency.

> **The plumbing for a position signal would be cheap; the signal is what is missing.**
> `ExecutableCodeBlockRenderer` already emits its marker as
> `<div class="md-code-cell-toolbar" data-submission-id=… data-language=…>`, so stamping a
> `data-content-language` there and cascading it is a small change. That is worth knowing precisely
> because it removes plumbing cost from the argument: option (a) is not rejected because it would be
> hard to wire, but because **there would be nothing true to put in the attribute** — see the next
> section.

### The code cell, concretely

> **Shipped 2026-09-04** (MeshWeaver.Plugins#1318). This section is kept in the present tense of the
> change because it is the worked example of clause 2, not a plan. What follows described the state
> before; the paragraph after the code block records what the button looks like now.

The change clause 2 asks for is one line in each of two files. The button **already** has everything
it needs:

```razor
<FluentButton Appearance="Accent" Title="@RunTitle" IconStart="@RunIcon" OnClick="@Run">
    @Access.Localize("common.run")     @* ← this, and only this, goes *@
</FluentButton>
```

`RunIcon` is already `Icons.Regular.Size16.Play()` (and `ArrowSync()` when the cell is stale).
`RunTitle` is already localized — `code.runCell` / `code.rerunCell` / `code.kernelUnavailable` — and
its own comment already gives clause 2's reasoning: *"a tooltip IS the control's accessible name, so
leaving it English would read as untranslated to exactly the users who need it most."*

So the glyph-plus-localized-tooltip pattern is fully built. The only thing to remove is a redundant
translated word. `code.staleCell` (*"Code changed — re-run"*) cannot become a glyph without losing
its meaning; it stays, and it is already inside the toolbar's own visually distinct band
(`--neutral-layer-2` with a top border), which is where clause 2 puts a word that cannot be removed.

**And the two Run buttons disagreed with each other**, which was worth fixing in the same change
because it shows the defect is not really about language policy. `CodeViews.BuildCellToolbar` took
`string? locale = null` and its only call site passed nothing, so
`LocalizationCatalog.Get("common.run", locale)` fell through to English: a **Code node page rendered
"Run" for a German viewer** while a code cell in a markdown body rendered "Ausführen" for the same
viewer on the same portal. The same file passed `locale: host.ViewerLocale()` correctly for its nav
menu, so it was an omission, not a decision. Under clause 2 both labels went and the disagreement
went with them — along with `Cancel`, `Edit`, *Code changed — re-run* and *Running…*, English-pinned
on that path for the same reason.

Both files are declared in MeshWeaver.Plugins, so the change landed there, not here
(MeshWeaver.Plugins#1318). The name did not become English and it did not disappear: the component
was rendered through the real Blazor renderer in both shipped languages and in all three states, and
both `title` and `aria-label` carry the localized text while the button's own content is empty.

| state | en | de |
|---|---|---|
| run | Run this code block | Diesen Codeblock ausführen |
| re-run (stale) | The code changed since this output was produced — run it again | Der Code hat sich seit dieser Ausgabe geändert — erneut ausführen |
| kernel unavailable (disabled) | Interactive kernel is not available here | Der interaktive Kernel ist hier nicht verfügbar |

🚨 **One lever that turned out not to move it, recorded rather than hidden:** FluentUI 4.14.4's
`FluentButton` mirrors `Title` into `aria-label` itself, so deleting the explicit `aria-label` alone
changes no byte of the DOM. A guard written only against that deletion would have been a guard that
cannot fail.

## What identifies content language today

**Nothing, on a `MeshNode`.** The record carries `Id`, `Namespace`, `MainNode`, `Name`,
`Description`, `NodeType`, `Category`, `Icon`, `Order`, the authorship and version fields, `State`,
`Content`, `PreRenderedHtml`, `DesiredId`, `IsSatelliteType`, `ExcludeFromContext` and
`SyncBehavior`. There is no language, locale or culture field, and the markdown front-matter parser
(`MarkdownFileParser.MarkdownFrontMatter`) is an explicit allowlist with no language key.

Two things that look like one and are not:

- **`ILocalizedNodeText.Translations`** declares which languages a node has *display overrides* for —
  `name`, `description`, `category`, and nothing else. Its own doc scopes it: *"This record touches
  display text only"*, explicitly not model-facing and not addressable. It says a node **has** a
  German name; it never says the node **was written in** German.
- **`AccessContext.Locale` / `User.Locale`** is the viewer's preference. It has never been about
  content.

**One real signal exists, and it is space-granular.**
`MeshWeaver.Plugins/Store/Core/Source/PluginContent.cs` carries `string? Language` on a
`Store/Plugin` root, and its doc states the platform's convention outright:

> BCP-47 language tag of this plugin's CONTENT (e.g. `en`, `de-CH`). The localization convention is
> **ONE space per language**: a translation is a SEPARATE `Store/Plugin` root (its own partition,
> price, gating and entitlements), **never mixed-language content inside one space**. Null =
> unspecified (the original language).

That is already a decision about granularity, and it is the opposite of a per-node field. Measured
coverage: **5 of 10 course roots in the education repo declare it** (`AgenticPrimer` `en`,
`AgenticPrimerDe` `de-CH`, `AgenticOffice` `de-CH`, `AgenticBusiness` `en`, `DataImportExport` `en`;
`AgenticEngineering`, `DataModeling`, `ThinkInStreams`, `AdvancedBusinessRules` and `WhatsNew`
declare nothing). Across the core repo's `samples/` tree, **530 node JSON files declare it zero
times**. Every other `language` in the fleet is a *programming* language (`csharp`, `python`,
`typescript`) or a speech/maps API parameter.

### What a per-node content-language field would cost

Not proposed — recorded so the cost is a measured number rather than a feeling, and so the next
person to suggest it starts from here.

| Where | What |
|---|---|
| Schema | one nullable column at **three** DDL sites in `PostgreSqlSchemaInitializer` (the `public` script, the versioned partition DDL, and the `ensure_partition_schema` proc body, which embeds the same string) |
| Migration | a `V56` migration over every partition schema, plus `DbVersion.Latest` 55 → 56. `DbVersionGate` stops a portal whose schema is behind, so this is a coupled deploy, not a code change |
| Adapter | `PostgreSqlStorageAdapter` — guarded column helper, four SELECT lists, the upsert insert/update/CAS fragments, the reader; plus `PostgreSqlCrossSchemaQueryProvider` and `PgMeshNodeReader` |
| Other backends | Cosmos, Snowflake and Sqlite adapters drift silently if missed |
| Import/export | `MarkdownFrontMatter` allowlist **and** the matching serializer write, under its byte-stability ordering rule. `.json` nodes round-trip free; `.md` nodes silently import null until this is done |
| Wire | the MCP `patch` tool's field allowlist refuses any field not on it |
| Equality | `MeshNode.Equals` enumerates its comparison fields **by hand**; a field missed there is invisible to change detection |

`V46_AddExcludeFromContextColumn` is the precedent and the cautionary tale: `ExcludeFromContext`
existed on `MeshNode` for a long time with no column, so *"the adapter never wrote/read it — on every
PG-backed mesh the field silently round-tripped as NULL."*

**And the cost above buys the smaller half of the problem.** The larger half is that nobody would
populate it. The one place the fleet already declares content language, at a granularity the
convention endorses, is **half empty**. A per-node field defaults to null on every node that exists
today and on every node an author creates without thinking about it — and a language rule keyed on a
field that is usually null is a rule that usually does nothing, while looking from the code as though
it does something.

## When the content language is unknown

**The rule never asks.** That is the point of making ownership the classifier: rendering authored
text requires no knowledge of what language it is in, and platform text follows the viewer, who has
already stated a preference (or been seeded one from `Accept-Language`, or falls back to English).
Neither branch consults the content's language, so "unknown" has no branch to take.

The one place content language *is* consulted today survives untouched and is a **fallback, not a
primary**: `EduTexts.ResolveChrome(viewerLocale, courseLanguage)` uses the course's declared language
only when the viewer states nothing this table carries — an anonymous reader, essentially. It reads
its signal off the course root it already owns, and it names which signal decided
(`LocaleSource.Viewer` / `.Course` / `.Default`) so a fallback is a stated fact rather than something
inferred from the rendered page. That shape is correct and should be the template for any module that
needs the same fallback.

## Adoption

**There is no content migration.** No node changes, no schema changes, no backfill, no re-import. No
existing content page changes meaning, and no author has to do anything. The work is a bounded code
change list, and most of it lands in MeshWeaver.Plugins because that is where the in-flow controls
live — core owns the rule and one unowned pair.

**Landed.**

- **The rule itself**, here and in [Localization](../Localization) — the page a contributor is
  pointed at from `AGENTS.md` carries clause 1 and clause 2, so the rule is readable where it is
  looked for rather than only in this companion page.
- **The Edu quiz has an owner.** `Edu/Quiz` and `Edu/Workbook` went from **zero** localization calls
  to the module's own text table
  ([MeshWeaver.Plugins#1261](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1261), closed by
  MeshWeaver.Plugins#1280). *Question 6 of 6* is now *Frage 6 von 6* for a German reader.
- **The copy-to-home dialog title** reads its catalog key (`ui.copyToHomeTitle`, added to the core
  catalog by [#3219](https://github.com/Systemorph/MeshWeaver/pull/3219)), as does *Read-only
  content* (`ui.readOnlyContent`).
- **The two code-cell toolbars dropped their translated label** (clause 2, MeshWeaver.Plugins#1318),
  and `CodeViews.BuildCellToolbar`'s call site now passes the locale, so the two Run buttons no
  longer disagree. This was the symptom #3203 reported. *Copy code* is announced in the viewer's
  language too — the fenced block's copy affordance had a translated tooltip over an English
  `aria-label`, so a German reader hovered *Code kopieren* while their screen reader said *Copy
  code*. Details and the rendered-in-both-languages evidence are in
  [The code cell, concretely](#the-code-cell-concretely) above.
- **The kernel placeholders follow the viewer.** `MarkdownViewLogic.DisableKernelPlaceholder` and
  `PendingKernelPlaceholder` render `code.kernelDisabledNotice` and `code.kernelStarting` in the
  language the caller passes, reached through a locale-carrying **overload** of each (and of
  `RenderKernelResultAreas`, which is what the two Blazor views actually call). Overloads rather
  than an added parameter: adding a parameter to an existing public method is a binary break, and an
  already-compiled module bound to the old signature would stop loading.
- **The guard exists**, in two halves — see Enforcement below.

**Outstanding.**

1. **Five `CodeViews` literals still render English for a German viewer** — the dialog title *"Save
   Failed"* (5 sites), `"Code Files"`, `"Loading code…"`, `"Enter display name…"` and *"never
   executed"*, all in `MeshWeaver.Plugins/src/MeshWeaver.Graph.Views/CodeViews.cs`. Their **core
   catalog keys now exist** (`code.saveFailed`, `code.codeFiles`, `code.loadingCode`,
   `code.enterDisplayName`, `code.neverExecuted`) — none had one before, which is why this could not
   ship from Plugins alone. What remains is consuming them there. Tracked as
   [MeshWeaver.Plugins#1308](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1308).
2. **Mirror the seven new core keys** into `MeshWeaver.Plugins/clients/react/src/i18n/`
   (`npm run sync:i18n -- --ref <merged core sha>`). 🚨 Adding a key in core reddens **nothing**:
   the drift guard compares against a **pinned** core commit (`catalog-source.json`), not core's
   `main`, so an unsynced mirror is silently stale rather than loudly broken. Core is the source of
   truth and merges first; the sync is a hand-over, not a repair.

The owner for a module's strings is the module's own text table, following `EduTexts`, which makes
EN/DE parity a **compile** error because every member is `required`; the owner for a platform string
is the core catalog. The kernel placeholders took the catalog because `MeshWeaver.Markdown` is
platform code, not a module.

## Enforcement — and what cannot be enforced

Be precise about which half is mechanical.

**Mechanical, and BUILT — in two halves, because no single project can see the whole set.** Clause 2
over the enumerated in-flow set is testable: assert that each in-flow control renders no localized
*visible* label, and that its tooltip/accessible name *is* a catalog lookup.

- **`InFlowChromeClause2Guard`** (MeshWeaver.Plugins, MeshWeaver.Plugins#1318) covers the three
  surfaces declared there — `MarkdownCodeCellToolbar`, `CodeViews`'s run-button area, and
  `CodeBlock.razor` — in a rendered half (a real `HtmlRenderer`, `en` and `de`) and a pure half. It
  was made to fail on purpose four ways: restore the label (*found "Ausführen"*), delete the
  accessible name (*no element carries that accessible name at all*), un-localize the name, and
  restore the label on the Code-node twin.
- **`KernelPlaceholdersFollowTheViewerGuard`** (`test/MeshWeaver.Documentation.Test/`, this repo)
  covers the two declared here. Its subject is clause **1** rather than clause 2 — a sentence
  explaining why execution is unavailable has no glyph equivalent, so nothing is dropped; what is
  pinned is that the text comes out of the catalog in the language the caller passed. Also made to
  fail on purpose: a hard-coded English literal (fails `de`, passes `en`), a locale dropped at
  `RenderKernelResultAreas` while both leaf helpers stay correct, and a **typo'd key name** — which
  every value-comparing assertion survives, because both sides resolve to the same wrong string, and
  which is caught instead by two languages resolving to identical text.

A core test cannot see a Plugins component, which is why the split exists rather than one guard. The
set is enumerated deliberately — a guard over "every control everywhere" would be a guard nobody can
keep green, and [CI](/Doc/Architecture/ReadingCiSignals) green on a guard that cannot fail is worse
than no guard.

**Already mechanical.** `LocalizationTest.EveryShippedLanguage_CoversEveryEnglishKey` asserts full
en/de key parity over **every** English key, currently zero missing. (No count is written here on
purpose: a number in prose goes stale on the next key added, and the test derives its own.)
#3203's option (b) asks for "a
completeness check so a missing key cannot fall back to English mid-page"; **that check already
exists and already passes.** The plugins mirror's `localize.test.ts` goes further and compares
values.

**NOT mechanical, and this is the honest limit.** No test can find clause 1's real failure — a string
that never became a key at all. `UserPreferencesLocalizationTest` states it exactly: *"a string that
never became a key is not a missing key, it is an absent one."* A hard-coded literal in a view is
invisible to every catalog guard by construction. The proof is `Edu/Quiz`: it contained **zero**
localization calls, so every catalog guard in the fleet stayed green while a whole feature rendered
one language, for as long as it took a reader to notice and file #3203. The controls that exist for
this are review and the enumerated in-flow guard; there is no third one, and claiming otherwise
would be the kind of guard-that-checks-nothing this repo has been bitten by before.

**Also not mechanical, and worth saying because it is the same shape:** the localization docs used to
cite a guard that no longer exists. `AnonymousCircuitLocaleSeedTest` was lost when
`MeshWeaver.Hosting.Blazor` moved to MeshWeaver.Plugins and its sibling tests moved without it, so the
anonymous locale seed has zero coverage on either `main` while prose still described it as what stops
the defect regressing
([MeshWeaver.Plugins#1273](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1273)).
[Localization](../Localization) now names it as **absent** rather than as coverage. A rule that is
only prose decays exactly this way, which is the argument for keeping clause 2's guard small enough
to survive a move.

## The trade-off being accepted

Stated plainly, because it is real.

**This rule does not deliver a monolingual page.** A German reader of an English course still gets a
German node menu, German section headings and English lesson prose. What it removes is the *intrusion*
— a translated word sitting inside the author's sentence. The thesis is that a page where every word's
language is explained by **whose word it is** reads as designed, while a page where a German verb
appears mid-paragraph reads as broken. That is a claim about how the seam is perceived, and it is the
claim the maintainer accepted when adopting this rule.

The other two costs:

- **Glyph-only controls are less discoverable.** A ▶ is conventional and safe; the next in-flow
  control may have no conventional glyph, and a tooltip needs a hover that a touch device does not
  have. Clause 2's escape — a word inside a visually distinct chrome band — is a weaker answer than
  removing the word, and it will get used.
- **We are declining to make the platform content-language-aware.** Features that would genuinely
  need it — per-language search analysers, TTS voice selection, machine translation, telling a reader
  "this is in German" before they open it — will have to add the field then, at the cost tabled
  above. This defers that; it does not solve it.

## What this does not decide

- Whether a **module** may ever choose content-language chrome for a surface it fully owns. The rule
  says no by default; a module with a genuine case should argue it against the Edu revert above, not
  around it.
- The **Edu quiz strings themselves** — they were owned by MeshWeaver.Plugins#1261, not by this page,
  and that issue is closed: the quiz now reads its module's text table.
- Whether `PluginContent.Language` should become **required** for a published content package. It is
  the one signal that exists and it is half populated; requiring it is a Store decision, not a
  localization one.

## See also

- [Localization](../Localization) — the catalog, `[Translation]`, where the viewer's language comes
  from, and the existing viewer-message / owner-diagnostic boundary this page's rule sits beside.
- [User Interface](../UserInterface) — the glyph-plus-tooltip preference clause 2 makes binding.
