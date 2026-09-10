---
nodeType: Markdown
name: Language Services (LSP-style)
category: Architecture
description: Roslyn-backed in-process language services over a NodeType's live CSharpCompilation — speculative pre-flight diagnostics, hover, completions. Consumed by code-authoring agents (Lsp plugin, driven by the /code skill), external MCP clients (McpMeshPlugin), and Monaco live squiggles in the portal Edit view.
icon: /static/NodeTypeIcons/code.svg
---

# Language Services

MeshWeaver surfaces Roslyn's full language intelligence — diagnostics, hover, and completions — directly over each NodeType's live `CSharpCompilation`. Three consumers share one in-process backend, all exposed through a single unified interface.

| Consumer | How it connects | What it uses |
|---|---|---|
| **Code-authoring agents** (the `/code` skill) | `Lsp` plugin (opt-in per agent via `plugins: - Lsp`) | Pre-flight diagnostics before committing a `Patch` |
| **External MCP clients** (Claude Code, etc.) | `Lsp*` tools on `McpMeshPlugin` | All four tools |
| **Monaco editor in the portal** | `IMeshLanguageService` resolved from DI | Live squiggles on any `Code` node under a NodeType's `Source/` subtree |

Behind all three sits one `IMeshLanguageService` interface with one in-process implementation: Roslyn's `CompletionService`, `QuickInfoService`, and a per-NodeType `AdhocWorkspace` built over the cached `CSharpCompilation` produced by `MeshNodeCompilationService`.
<svg viewBox="0 0 760 370" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="370" rx="4" fill="#1a1a2e"/>
  <rect x="30" y="20" width="170" height="52" rx="10" fill="#1e88e5"/>
  <text x="115" y="41" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Code Agents</text>
  <text x="115" y="58" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">Lsp plugin (opt-in)</text>
  <rect x="295" y="20" width="170" height="52" rx="10" fill="#8e24aa"/>
  <text x="380" y="41" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">MCP Clients</text>
  <text x="380" y="58" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#e1bee7">Claude Code / external</text>
  <rect x="560" y="20" width="170" height="52" rx="10" fill="#26a69a"/>
  <text x="645" y="41" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Monaco Editor</text>
  <text x="645" y="58" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b2dfdb">Portal Edit view</text>
  <line x1="115" y1="72" x2="340" y2="128" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="72" x2="380" y2="128" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="645" y1="72" x2="422" y2="128" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="175" y="130" width="410" height="68" rx="10" fill="#37474f"/>
  <text x="380" y="154" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">IMeshLanguageService</text>
  <text x="380" y="172" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">GetDiagnostics · GetHover · GetCompletions · CheckSpeculative(Outcome)</text>
  <text x="380" y="188" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#90a4ae">all return IObservable&lt;T&gt;</text>
  <line x1="380" y1="198" x2="380" y2="230" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="175" y="232" width="410" height="72" rx="10" fill="#455a64"/>
  <text x="380" y="254" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">MeshNodeLanguageService</text>
  <text x="380" y="272" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">AdhocWorkspace per NodeType (keyed by source-versions hash)</text>
  <text x="380" y="288" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">Roslyn CompletionService · QuickInfoService · CSharpCompilation</text>
  <line x1="280" y1="304" x2="215" y2="330" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="480" y1="304" x2="545" y2="330" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="30" y="332" width="340" height="28" rx="8" fill="#1e88e5" fill-opacity="0.7"/>
  <text x="200" y="351" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">MeshNodeCompilationService</text>
  <rect x="390" y="332" width="340" height="28" rx="8" fill="#f57c00" fill-opacity="0.8"/>
  <text x="560" y="351" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">SpeculativeCompilation</text>
</svg>
*Three consumers share one `IMeshLanguageService` backed by Roslyn — the /code pre-flight, MCP tools, and Monaco live squiggles all route through the same in-process pipeline.*

## Architecture

```
   ┌──────────────────────────────────────────────────────┐
   │       IMeshLanguageService (Mesh.Contract)           │
   │   GetDiagnostics / GetHover / GetCompletions /       │
   │   CheckSpeculative / CheckSpeculativeOutcome         │
   │   — all return IObservable<T>                        │
   └────────────────────────┬─────────────────────────────┘
                            │
                            ▼
   ┌──────────────────────────────────────────────────────┐
   │       MeshNodeLanguageService (Graph)                │
   │   • Per-NodeType AdhocWorkspace, cached by source-   │
   │     versions hash (rebuilt when any source changes). │
   │   • One Document per user source (FilePath = MeshNode│
   │     path) + one __skeleton__ document carrying the   │
   │     generated MeshNodeProviderAttribute boilerplate. │
   │   • Roslyn CompletionService / QuickInfoService /    │
   │     CSharpCompilation.GetDiagnostics queries.        │
   └──┬─────────────────────────────────────────────────┬─┘
      │                                                 │
      │ shared CompilationInputs (per-file syntax       │
      │ trees, MetadataReferences, skeleton source)     │
      │                                                 │
      ▼                                                 ▼
   ┌─────────────────────────────┐   ┌─────────────────────────────┐
   │  MeshNodeCompilationService │   │  SpeculativeCompilation     │
   │  .GetCompilationInputsAsync │   │  .GetDiagnosticsAsync       │
   │  (NodeType source set,      │   │  (substitute one file with  │
   │   @@-includes resolved,     │   │   proposedCode, strip & re- │
   │   NuGet refs resolved,      │   │   resolve #r, rebuild, get  │
   │   skeleton generated)       │   │   diagnostics)              │
   └─────────────────────────────┘   └─────────────────────────────┘
```

> **Why a parallel `GetCompilationInputsAsync` distinct from the emit path?**
> Language services need per-file syntax trees so positions map back to what the user is editing, while the emit path concatenates all sources into one tree to produce a single assembly. The parallel pipeline keeps the production compile untouched and lets the language service work with the per-file shape without any behavioural risk.

## The MCP and Agent Surface

Two tools are exposed via `McpMeshPlugin` for external MCP clients (`lsp_check_node`, `lsp_diagnostics_for_node`); the in-portal `Lsp` agent plugin additionally carries hover and completions for agents that opt in via `plugins: - Lsp` (the built-in Assistant, Worker, and Email Router declare it). Hover/completions were removed from the MCP surface in the 2026-06-11 tool-surface compaction — position-based lookups are an IDE interaction shape, and an agent driving JSON tool calls reads source via `get` and runs the pre-flight check instead. `IMeshLanguageService` retains all four capabilities for first-party UI (Monaco) use.

| Tool | Surface | Purpose | Returns |
|---|---|---|---|
| `LspCheckNode` | MCP + agent plugin | Speculative pre-flight against the NodeType's current source set with one file substituted. Reuses the cached `MetadataReference` set, strips `#r "nuget:..."` directives, resolves any new packages. | `{ok, diagnostics: [...]}` |
| `LspDiagnosticsForNode` | MCP + agent plugin | Diagnostics from the NodeType's current cached compilation — no substitution, no recompile. Faster than `Compile` for "what does Roslyn currently think?" | `{ok, diagnostics: [...]}` |
| `LspHoverForNode` | agent plugin only | QuickInfo (signature + XML doc summary) at a position, rendered as markdown. | `{markdown}` or `{}` |
| `LspCompletionsForNode` | agent plugin only | Code completions at a position, mapped from Roslyn's `WellKnownTags` to LSP-style kinds. | `{items: [{label, kind, insertText, detail?, sortText?}]}` |

**Position convention.** All positions are **0-based line/character** (LSP convention). Monaco's 1-based coordinates are converted at the JS bridge.

**`{ok: true}` semantics.** This means Roslyn ran **and** there are no `Error`-severity diagnostics — warnings alone don't fail the check, mirroring how a real `Compile` succeeds with warnings. Both halves are load-bearing; the section below is why.

## An empty diagnostic list is not evidence of health

🚨 **A verification step that cannot fail is not a verification step**, and this surface has now produced that defect twice — the second time in the method next door to the one that was fixed.

`[]` is what a clean compile looks like. It is *also* what "I could not resolve that path" looks like, and what "the owning hub never answered" looks like. Nothing in an `IReadOnlyList<DiagnosticInfo>` separates them, so a tool rendering `ok = !diagnostics.Any(Error)` reports a **clean bill of health for a check that never ran**.

| Method | Fixed in | The symptom |
|---|---|---|
| `GetDiagnostics` | #1592 / #1618 | `lsp_diagnostics_for_node @Edu/DefinitelyNotARealNodeType` → `{"ok":true,"diagnostics":[]}`; the mandated pre-prod sweep reported all-green over stale paths having verified nothing |
| `CheckSpeculative` | #3888 | `lsp_check_node` on a NodeType in a partition the caller cannot read → `{"ok":true,"diagnostics":[]}` — **including for `proposedCode` that is not C# at all**; the `/code` edit loop's pre-flight blessed every proposed edit against every path it could not reach |

**Why one fix did not cover the other, and why it looked reasonable at the time.** One return type was serving two consumers whose needs are *opposite*:

- the **Monaco editor** wants silence when the owner cannot be resolved — a squiggle computed under the wrong language rules is worse than no squiggle;
- the **`lsp_check_node` tool** renders a **verdict**, and silence there reads as *approved*.

The comment in the code said "stay silent rather than paint squiggles computed under the wrong language rules". That is correct — for one of the two callers. The compromise return type made it wrong for the other, invisibly.

**The shape of the fix: two surfaces, not one compromise.**

| Method | Returns | For |
|---|---|---|
| `CheckSpeculative` | `IReadOnlyList<DiagnosticInfo>` | the **editor**. Deliberately silent when nothing was checked — Monaco's contract is "no diagnostics means no squiggles" |
| `CheckSpeculativeOutcome` | `NodeDiagnosticsOutcome` | **every caller that renders a verdict** — the MCP tool, the agent plugin, any pre-flight gate |

`NodeDiagnosticsOutcome` carries `Compiled` / `Absent` / `NotCompilable` / `Unavailable` plus `IsClean` (false for every status that did not actually compile) and `DescribeProblem(path)` (the reason, with the path in it, so a sweep's output says *which* entry could not be checked). The list overload is **derived from** the outcome overload — `CheckSpeculativeOutcome(...).Select(o => o.Diagnostics)` — so the two can never drift apart about what a given path produces; the only difference is that one drops the status.

The interface member carries a **default implementation that answers `Unavailable`**, deliberately fail-closed. It exists so an addition cannot oblige every implementer at once (an addition's core half lands first — see [Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate)), and an implementation that has not supplied one has genuinely not checked anything. Mapping the list onto `Compiled` in the default would have re-created the defect for every implementer that never noticed the member appear.

> **What #3888 also corrected about its own reading of the code.** The issue named *two* silent branches and could not say which produced the live observation. It can only have been the unresolvable-owner one: the other — `GetCompilationInputsAsync` answering `null` — is unreachable from the speculative path, because that method refuses exactly one shape (a node whose `NodeType` is unset) and such a node is classified as a **script** here, never as a NodeType. The `NotCompilable` mapping stays as the honest reading of a nullable contract, and the test suite says so rather than pinning a branch that could never fail.

The controls live in `test/MeshWeaver.Compiler.Pipeline.Test/SpeculativeCheckCannotAnswerGreenForAnUncheckedNodeTest.cs` and run **in both directions**: an unresolvable path must not read clean, *and* a real NodeType must still compile its proposal and report errors when the proposal is broken. A fix that made everything fail would be no better than one that made everything pass. See [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail).

> **One arm no live-mesh test can reach — and what to do about it.** The wedged-owner case
> (`NodeReadStatus.Unavailable`) cannot be arranged on demand: the read's budget is 15 s and the
> outcome depends on an owner that will not answer. An arm with no control is precisely where a
> regression would put `Compiled` back unnoticed, so the mapping is **extracted** —
> `MeshNodeLanguageService.NothingWasChecked(NodeReadOutcome)`, the whole decision rather than a
> fragment, the same idiom as `CanReuseWorkspace` — and driven directly over
> `Enum.GetValues<NodeReadStatus>()`. The invariant that gets asserted is deliberately stronger than
> per-value equality: **no** read that produced no node may render as `Compiled`, *including a status
> added later*. A per-value test would go silent on a new status; enumerating the enum makes the next
> one arrive as a red instead of as a hole.

## The /code Pre-Flight Loop

Before any non-trivial source change, an agent operating under the [/code skill](/Skill/code) follows this sequence:

1. Read current source (`Get` if not already in context).
2. Call `LspCheckNode({nodeTypePath, sourcePath, proposedCode})`.
   - `{ok: true, diagnostics: []}` → safe; persist via `Patch`.
   - `{ok: false, diagnostics: [errors...]}` → fix in head, re-call.
3. Once clean, issue `Patch` / `Update`.
4. `Compile` + `GetDiagnostics` for the real emit.

This loop replaces the old blind `Patch → Compile → Recycle → fix` cycle that previously dominated CI failures.

> **Full-substitution semantics.** `LspCheckNode` rebuilds the whole NodeType source set with the one proposed file substituted in. This catches the dominant code-edit failure mode where editing one file breaks a sibling — rename a type in `A`, and `B`'s reference still points at the old name. Single-file isolation would miss this entirely.

> **`#r` support.** The proposed source can include `#r "nuget:PackageId, Version"` directives — the speculative compile strips them and resolves the packages through the same `INuGetAssemblyResolver` the production compile uses. New diagnostics reflect what `Compile` will actually see.

The full coding workflow is in the [/code skill](/Skill/code).

## Monaco Live Diagnostics

The portal's **Edit** view on any `Code` MeshNode under a NodeType's `Source/` subtree displays live Roslyn squiggles. The wiring runs through four layers:

1. **Opt-in** — `CodeLayoutAreas.Edit` derives the NodeType path from `host.Hub.Address` (the Code node's path) and sets `CodeEditorControl.LanguageServer = new CodeEditorLanguageServerConfig(nodeTypePath, sourcePath)`. Standalone Code nodes (not under `/Source/`) and non-C# languages skip the opt-in.

2. **Blazor bridge** — `CodeEditorView.razor` resolves `IMeshLanguageService` from DI and passes a `DiagnosticsCallback = text => languageService.CheckSpeculative(nodeTypePath, sourcePath, text)` to `MonacoEditorView`. The `DiagnosticSourcePath` filter keeps squiggles scoped to this file; sibling-file diagnostics from the full-substitution compile are suppressed.

3. **JS debounce** — `MonacoEditorView.razor` exposes a `RequestDiagnostics` JSInvokable. The JS module debounces `onDidChangeModelContent` (~300 ms) and pings .NET with the current text.

4. **Marker push** — The callback returns `IObservable<DiagnosticInfo[]>`; the first emission is pushed to JS via `pushDiagnostics`. JS converts to Monaco `IMarkerData` (0→1 coords, severity 0..3 → 1, 2, 4, 8) and calls `monaco.editor.setModelMarkers(model, 'meshweaver-lsp', markers)`.

The pattern mirrors the existing reactive completion provider — no new transport, no `monaco-languageclient`, no LSP-over-WebSocket. Blazor's existing JS interop over SignalR carries everything.

## Cache Semantics

`MeshNodeLanguageService` caches one `AdhocWorkspace` per NodeType, keyed by a source-versions snapshot (`{sourcePath → MeshNode.LastModified.Ticks}`). When any source file changes the snapshot diverges and the workspace is rebuilt.

The in-process `MeshNodeCompilationService._references` field (TPA + a few well-known additions) is `private static readonly`, initialized once and never written at runtime, so reference resolution cost is paid once per process. That is the sanctioned *immutable constant lookup* case — it is **not** a licence for a static cache. Anything that takes a write at runtime must be an instance field on a mesh-scoped singleton (see [No Static State](/Doc/Architecture/NoStaticState)); the per-NodeType workspace cache above is exactly that.

Speculative compiles do **not** cache — every `LspCheckNode` call rebuilds the substituted compilation. Cost is dominated by parse + bind + diagnose (~200–500 ms for typical NodeTypes). No emit, no DLL on disk.

## Reactive Contract

🚨 **Every `IMeshLanguageService` method returns `IObservable<T>`.** The Roslyn `*Async` leaves are bridged through the **bounded Compile I/O pool** — `_ioPool.Run(ct => GetXxxAsync(cached, ct))`, where `_ioPool` is `IoPoolRegistry.Get(IoPoolNames.Compile)` (falling back to `IoPool.Unbounded` only when the service is constructed outside DI, e.g. in a test). Roslyn LSP work is CPU-bound, and the bounded pool caps its concurrency so it cannot starve other schedulers.

> 🚨 **Never `Observable.FromAsync` here — or anywhere in `src/`.** A bare `FromAsync` runs the function's synchronous prologue on the *subscribing* thread and applies no concurrency bound; under a blocking subscriber it deadlocks, because `SubscribeOn` moves only the subscribe, not the `await` continuation. The pool runs the leaf with `ConfigureAwait(false)` behind a gate, which is what makes it safe. See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

MCP and agent tool surfaces have a `Task<string>` signature that is not ours to change, so they bridge once at that edge — with `.FirstAsync().ObserveCompletion(reportLateFault, ct)`, never `.ToTask()` (forbidden repo-wide as of 2026-08-30) and never a bare `await someObservable`, both of which resume the adapter inline on the mesh thread that answered. Historically this read `.FirstAsync().ToTask()`, which was the sanctioned exception for external-protocol adapters (see [AsynchronousCalls](/Doc/Architecture/AsynchronousCalls)). No `await` anywhere in hub-reachable code.

## What's Deferred

The following capabilities were considered but are not yet implemented:

- **Stage 2: repo-level LSP** for the MeshWeaver framework itself (50+ projects, on-disk `.slnx`). Originally planned as a separate CLI host spawning `Microsoft.CodeAnalysis.LanguageServer` for Claude Code's consumption. Skipped for now — the in-portal surface (Stage 1) covers the immediate /code workflow.

- **Monaco hover + completions.** `MonacoEditorView` only consumes the `DiagnosticsCallback` today. Hover and completion providers would be additional JSInvokables plus Monaco `registerHoverProvider` / `registerCompletionItemProvider` calls, but they need overlay support in `IMeshLanguageService` (in-flight editor text rather than saved text) before they're useful — a separate API extension.

- **Cross-NodeType go-to-definition** (jump from a NodeType source into framework symbols). Would require unifying the in-portal compilation with a framework workspace; not feasible without significant scaffolding.

- **Auto-wired NodeType-hub diagnostics view** — a layout area that surfaces `LspDiagnosticsForNode` results live on the NodeType itself. Planned follow-up.

## Where Things Live

| Concern | File |
|---|---|
| Interface + DTOs | `src/MeshWeaver.Mesh.Contract/Services/IMeshLanguageService.cs` |
| In-process implementation | `src/MeshWeaver.Compiler.Pipeline/MeshNodeLanguageService.cs` |
| Speculative compile + `#r` handling | `src/MeshWeaver.Compiler/SpeculativeCompilation.cs` |
| `CompilationInputs` (shared with emit path) | `src/MeshWeaver.Graph/Configuration/CompilationInputs.cs` |
| `GetCompilationInputsAsync` (the per-file pipeline) | `src/MeshWeaver.Compiler.Pipeline/MeshNodeCompilationService.cs` |
| DI registration | `src/MeshWeaver.Graph/Configuration/GraphConfigurationExtensions.cs` (`AddGraph`) |
| Agent plugin | `src/MeshWeaver.AI/Plugins/LspPlugin.cs` |
| MCP tools | `src/MeshWeaver.Mcp/McpMeshPlugin.cs` (the `Lsp*` methods) |
| Monaco wiring | `src/MeshWeaver.Blazor/Components/Monaco/MonacoEditorView.razor[.js]`, `CodeEditorView.razor` |
| Editor opt-in | `src/MeshWeaver.Layout/CodeEditorControl.cs` (`LanguageServer` property) |
| Edit-view opt-in | `src/MeshWeaver.Graph/CodeLayoutAreas.cs` (`Edit` area) |
| Integration tests | `MeshWeaver.Plugins/src/MeshWeaver.Hosting.Monolith.Test/MeshNodeLanguageServiceTest.cs` |
| The pre-flight's honesty controls | `test/MeshWeaver.Compiler.Pipeline.Test/SpeculativeCheckCannotAnswerGreenForAnUncheckedNodeTest.cs` |
