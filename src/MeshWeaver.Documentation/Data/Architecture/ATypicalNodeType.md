---
Name: A Typical NodeType
Category: Architecture
Description: One small NodeType with its tests, as the build queue runs them — Budget, shipped beside this page. The files, the rules, how the bake and the gate compile it and execute its Tests area on every pull request, what green means, and the traps already paid for.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="3"/><path d="M7 16v-3M12 16V9M17 16V6"/></svg>
---

# A Typical NodeType

**Where in core can you see a NodeType and its tests as the build queue runs them?** Here:
`src/MeshWeaver.Documentation/Data/Architecture/ATypicalNodeType/Budget`, shipped with this page.
It is deliberately small — a content record, the rules derived from it, one view, a text table in
English and German, and a `Test/` folder with a `Tests` area. Every pull request to core compiles
it, renders it and **executes its tests** with the same tester binary and the same verdict
classifier the MeshWeaver.Plugins build queue uses, and turns red when a case fails.

It lives in the `Doc` tree rather than in `samples/Graph/Data` for one reason: a live embed must
target a partition that ships with the docs. The samples are not installed on the portals, so an
embedded sample node renders *"No renderer is registered"* there —
`DocumentationEmbedIntegrityTest` refuses it, and the shipped examples (`Cession`, `SocialMedia`)
live here for the same reason. Both trees go through the same gate.

## The live run

The instance `LaunchBudget`, embedded twice. **Rendering the `Tests` area runs the suite**, so the
table below is a fresh run on the portal you are reading this on, not a recording:

@@Doc/Architecture/ATypicalNodeType/Budget/LaunchBudget/area/Summary

@@Doc/Architecture/ATypicalNodeType/Budget/LaunchBudget/area/Tests

The same suite as core's content gate prints it, on the probe instance the gate creates:

```text
[PASS] Doc (1886 node(s), 5 type(s))
    ok  Doc/Architecture/ATypicalNodeType/Budget: compile=Ok render=ok tests=ok
        Tests host: Doc/Architecture/ATypicalNodeType/Budget/GateProbe — the probe instance the gate created for this check
        8/8 passed
    ok  Doc/Architecture/BusinessRules/Cession: compile=Ok render=ok tests=skipped
```

## The files

All paths are relative to `Architecture/ATypicalNodeType/`.

| File | What it is | Rule it shows |
|---|---|---|
| `Budget.json` | the NodeType node: `content.$type = NodeTypeDefinition` and the `configuration` lambda | the Tests area is registered with a **literal** `WithView("Tests", BudgetTestsArea.Tests)` inside the lambda |
| `Budget/Source/Budget.cs` | the content record, the `BudgetHealth` constants, the pure `BudgetRules` | derived values are computed, never stored; the vocabulary is **open string constants**, never an enum |
| `Budget/Source/BudgetTexts.cs` | the view's chrome in English and German | a module text table with `required` members, so a missing translation is a compile error |
| `Budget/Source/BudgetLayoutAreas.cs` | the `Summary` area, the node's landing page | registered with `WithNodePage`; reads the node's OWN stream, types it with `ContentAs`, composes platform controls |
| `Budget/Test/BudgetTests.cs` | the cases — `public static` methods that **throw** on failure | no test framework, no mocks; references only its own `Test/` folder and the type's sources |
| `Budget/Test/BudgetTestsArea.cs` | the `Tests` area — the runner | lists every case; renders ONE verdict frame: an `N/M passed` title and a ✅/❌ grid |
| `Budget/LaunchBudget.json` | an instance, with `"$type": "Budget"` content | what the embeds above render |

Each `.cs` file opens with the `// <meshweaver>` block (`// Id:`, `// DisplayName:`) and with
`#nullable enable`. The Doc tree carries an EMPTY warning baseline, and a `string?` in a file with
no nullable context is a warning the bake refuses ([In-Mesh Warning Standard](../InMeshWarningStandard)).

### The configuration

```csharp
config => config.WithContentType<Budget>()
    .AddDefaultLayoutAreas()
    .AddLayout(layout => layout.AddBudgetLayoutAreas()
                               .WithView("Tests", BudgetTestsArea.Tests))
```

The type's own `Source/` and `Test/` folders are compiled with it; nothing is declared. A type that
needs another type's code lists it in `sources` (`shared=@Other/Type/Source`) — never another
type's `Test/`.

### The view

```csharp
public static IObservable<UiControl?> Summary(LayoutAreaHost host, RenderingContext context)
{
    var texts = BudgetTexts.For(host.ViewerLocale());
    var options = host.Hub.JsonSerializerOptions;
    return host.Workspace.GetMeshNodeStream()
        .Select(node => (UiControl?)View(node.ContentAs<Budget>(options), texts));
}
```

The area is a projection of the node's own stream: no query, no `async`, no copy of the content
in a `/data` section ([Data Binding](/Doc/GUI/DataBinding)). The composition is the pure
`View(budget, texts)`, which is what lets a test check exactly what the area renders.

### The cases

```csharp
public static void Health_FollowsSpendAgainstThePlan()
{
    Expect(BudgetRules.Health(Launch with { Spent = 900m }) == BudgetHealth.AtRisk,
        "exactly 90% is at risk — the boundary is inclusive");
    …
}
```

Most cases are pure: the health boundary, remaining going negative, a negative spend refused, an
unknown health shown as itself, the figures in English and German number formats, and the control
composition. Two take the area's host: content arriving as JSON types back through this hub's
serializer, and the live `Summary` area agrees with the host node's content.

## How the build queue compiles and runs it

Core's `doc-gate` job, step *Compile + run the Doc content against this PR*, stages the Doc tree
(`.github/scripts/stage-doc-gate.sh`) and runs `.github/scripts/bake-then-gate.sh` — the same split
main-cd's publish-bake and every node repo's gate make. The samples trees then go through the same
script in the same step.

1. **The bake — a compiler, no mesh.** `mw-plugin-test compile <stage> --output <bake>` compiles
   each NodeType alone: its sources concatenated into ONE unit with the `using`s hoisted, and the
   configuration lambda wrapped in the generated method. Both warning ratchets must print
   `ENFORCED`, or the script reds.
2. **The gate — a mesh that CONSUMES the bake.** `mw-plugin-test <stage> --seed <bake>` installs
   the tree into an in-process mesh and adopts each type's baked assembly. Per type it waits for
   `compile=Ok` and renders the type's default area. Then, because the lambda contains
   `WithView("Tests"`, it **creates a fresh probe instance** (`…/Budget/GateProbe`) and renders its
   `Tests` area. Rendering it runs the cases.
3. **The verdict.** `AreaProbe` reads the frame. Any ❌ is red; `N/M passed` is green only when
   N == M; no verdict within the budget is red. The type line and the `8/8 passed` detail above are
   its output, and `GATE FAILED — tests: …` is the job's.
4. **The tree check.** The step *Every Test/ folder's Tests area ran and counted its cases*
   (`.github/scripts/check-tests-area-verdicts.py`) reads the staged tree and the gate log together.
   A NodeType owning a `Test/` folder must report `tests=ok` with a count, and the Doc tree must own
   at least one. That closes the one hole the gate cannot see: a suite whose area is not declared
   literally reads `tests=skipped` and `ok`, and the gate prints `ALL GREEN.`.

MeshWeaver.Plugins runs the same tester from the platform image (`test-repos`), with its own
Tests-area ratchet over the same log (`check-test-suites.py`). The mechanism is the same; only the
reference set differs — Plugins compiles against the portal's `/app`, core against the tester's
own closure.

**Locally**, replay exactly that from the worktree root:

```bash
dotnet build tools/MeshWeaver.PluginTester/MeshWeaver.PluginTester.csproj -c Release -warnaserror
bash .github/scripts/stage-doc-gate.sh src/MeshWeaver.Documentation/Data /tmp/doc
bash .github/scripts/bake-then-gate.sh \
  tools/MeshWeaver.PluginTester/bin/Release/net10.0/mw-plugin-test.dll \
  /tmp/doc /tmp/bake/doc .github/doc-gate.allow "$(git rev-parse HEAD)" \
  /tmp/doc-gate .github/doc-gate-warnings.allow
python3 .github/scripts/check-tests-area-verdicts.py \
  --gate-log /tmp/doc-gate.log --tree /tmp/doc --require-tests
```

It takes seconds for the Doc tree on a laptop.

## What green means — and what it does not

| Green says | Green does not say |
|---|---|
| the type compiles as the mesh compiles it, with no reported warning | that anything a case does not assert is right |
| the default area rendered without a framework error | that the `Summary` area is right for every content — only the cases say that |
| every listed case ran and passed, and the count is the count | that a method **not listed** in `BudgetTestsArea` ran — it did not |
| the tests ran against the bytes the bake produced | that a portal already runs them: a merged change reaches a running hub only after a recycle ([Stale State Until Recycle](../StaleStateUntilRecycle)) |

## The negative controls, measured

Each was run with the local replay above against this change, then reverted:

| Perturbation | Gate | Tree check |
|---|---|---|
| `AtRiskThreshold = 0.95m` in `Budget.cs` | `RED …/Budget: compile=Ok render=ok tests=FAILED` · `❌ exactly 90% is at risk — the boundary is inclusive` · `GATE FAILED` · exit 1 | red |
| the `Summary` area renders a notice its content does not imply | `❌ the area's notice differs from the content's` · `GATE FAILED` · exit 1 | red |
| `WithView("Tests", …)` moved from the lambda into `AddBudgetLayoutAreas` | `ok …/Budget: compile=Ok render=ok tests=skipped` · **`ALL GREEN.`** · exit 0 | **red**: *ships a Test/ folder but reported tests=skipped* |

The third row is why the tree check exists. Its own `--self-test` pins both directions — inputs
that must pass and inputs that must fail — and dropping its `skipped` or truncated-log handling reds
that self-test.

## The traps already paid for

- **The area must be registered LITERALLY in the lambda.** The gate reads the configuration as text
  for `WithView("Tests"`. An area registered anywhere else reports `tests=skipped` and asserts
  nothing — measured above ([In-Mesh Build and Test](../InMeshBuildAndTest)).
- **A case not in the array runs nowhere.** There is no discovery in the area; one Plugins case
  existed, compiled and ran nowhere until someone noticed ([Decentralised Tests](../DecentralisedTests)).
- **The verdict token is not chrome.** `N/M passed`, ✅ and ❌ are the gate's wire format. German
  suites rendering `N/M bestanden` once passed with no count at all — so the title stays English
  while the `Summary` chrome follows the viewer.
- **`Test/` references only its own `Test/` folder.** The mesh compiles each NodeType alone; a
  helper from another type's `Test/` compiles in a merged local build and fails `CS0103` in CI
  ([Domain Configuration Apps](../DomainConfigurationApps)).
- **The probe has no content.** The gate runs the area on a fresh `GateProbe` it just created, so a
  case must be self-contained. The live `Summary` case therefore checks agreement with whatever the
  host holds — the empty notice on the probe, the figures on `LaunchBudget` — and the content
  behaviour is asserted by the pure cases on their own fixture.
- **Rendering the area RUNS it.** Every view of the embed above re-runs the suite, so a case must
  never write to its host. To run a suite from a terminal or an agent, use the `run_tests` activity
  (`memex tests @<node>`), never a poll of the area ([Writing Tests](../WritingTests)).
- **A cast is a silent null.** Content that crossed a hub arrives as JSON; `node.Content is Budget`
  reads null and the view renders empty. `ContentAs<Budget>(options)` is the read, and the
  round-trip case pins it ([CQRS and Content Access](../CqrsAndContentAccess)).
- **A Tests area that no required context executes is a latent trunk red.** This one is executed by
  `doc-gate`, which the required `Consolidate test results` needs
  ([In-Mesh Tests and the Seal](../InMeshTestsAndTheSeal)).
- **A landing page is registered with `WithNodePage`, never a bare `WithView`.** Only then does it
  carry the Type · Created · Updated line; a bare registration ships without it and says nothing.
  `NodePageProvenanceGuard` refused this example's first draft for exactly that.
- **An example node must ship with the docs.** Embedding a sample-partition node is refused by
  `DocumentationEmbedIntegrityTest`; this showcase started in `samples/Graph/Data/ACME` and moved
  here for exactly that.
- **Expected noise.** The gate log carries one `Received '$type':'Budget' which is NOT registered in
  this (receiving) hub` warning at install time, before the NodeType has compiled. Every content type
  in the samples trees logs the same line; it is not a failure of this example.
- 🚨 **`run-node-tests.py` does not run in a core checkout.** The canonical local harness
  ([The Canonical Node-Test Harness](../CanonicalNodeTestHarness)) refuses the reference set because
  `MeshWeaver.AI`, `MeshWeaver.Markdown.Collaboration` and `MeshWeaver.Maps` are missing. Those are
  modules that left core, so the `dotnet build src/MeshWeaver.AI` it suggests cannot be followed
  here. In core the local loop is the gate replay above; the harness is a satellite's fast loop.

## Adding your own

Copy the shape, not the domain: one content record, pure rules beside it, a view that projects the
node's own stream through a pure composition, a text table per language, a `Test/` folder with
throwing static cases, and a `*TestsArea` that lists every one of them — registered literally.
Then run the replay above, break one rule on purpose, and watch it go red before you trust the
green.

## See also

- [Adding a New Node Type](../AddingANewNodeType) — the compiled (C#-project) route
- [NodeType Compilation](../NodeTypeCompilation) — what the mesh compiles, and when
- [In-Mesh Build and Test](../InMeshBuildAndTest) — the test shape and its two executors
- [Reading CI Signals](../ReadingCiSignals) — why a skipped check reads as a pass
- [Open Vocabularies as String Constants](../OpenVocabulariesAsStringConstants)
- [Chrome and Content Language](../ChromeAndContentLanguage)
