# Chat Slash-Commands

The chat input supports slash-commands (`/agent`, `/model`, `/harness`, `/help`, …). The system is
built so a command is **emancipated from the GUI**: a command is a *handler* — like a button's click
action — that runs **in the thread** when the user executes it, and **triggers callbacks** to inject
any UI it needs (e.g. pop the mesh-node selector). A command never references Blazor, so a module
ships one and it works in the chat with **no change to the chat view**.

There are two ways to ship a command, and one extension point:

| Form | When | Where |
|---|---|---|
| **`nodeType:Command` mesh node** (data) | "pick a node → save it on the composer" — the common case | `BuiltInCommandProvider` + per-Space/NodeType/user partitions |
| **`MeshNodePickCommand`** (C# subclass) | same pick pattern but you want it in code | your module's DI |
| **`IChatCommand`** (C# handler) | a real workflow beyond a pick (`/help`, future actions) | your module's DI |

The standard `/agent`, `/model`, `/harness` are **mesh nodes**, not C# — see [the catalog](#the-standard-commands-are-mesh-nodes).

---

## The handler model

A command implements `IChatCommand`:

```csharp
public interface IChatCommand
{
    string Name { get; }            // the slash word, lowercase ("agent")
    string Description { get; }     // help text + autocomplete
    string Usage => $"/{Name}";
    IReadOnlyList<string> Aliases => [];

    void Execute(CommandContext context);   // the handler — sync, like a click action
}
```

`Execute` is **synchronous and reactive** — no `async`/`Task` (it runs in the Blazor view; see
[AsynchronousCalls](/Doc/Architecture/AsynchronousCalls)). It runs the command's workflow against the
thread and **triggers GUI callbacks** on the context. It returns nothing — feedback flows through the
callbacks, not a return value.

### The context: thread access + GUI callbacks

`CommandContext` is what decouples the command from the GUI. It carries the **thread** the command
runs in, and the **callbacks** the host (the chat view) wired:

```csharp
public record CommandContext
{
    public required ParsedCommand ParsedCommand { get; init; }  // name + args the user typed
    public IMessageHub? Hub { get; init; }                      // resolve services, write nodes
    public string? ContextPath { get; init; }                  // navigation context (scopes queries)
    public string? ThreadPath { get; init; }                   // the thread this runs in
    public string? ComposerPath { get; init; }                 // the ThreadComposer selections persist on
    public ChatCommandRegistry? CommandRegistry { get; init; } // for /help

    // GUI callbacks — the handler TRIGGERS these to inject UI, without referencing Blazor:
    public Action<NodePickerRequest>? ShowNodePicker { get; init; }   // pop the node selector
    public Action<string, bool>? ShowStatus { get; init; }            // status / error / help line
}
```

The callbacks are **optional** (null in a headless / test host), so a handler null-guards them
(`context.ShowNodePicker?.Invoke(...)`) and stays unit-testable with no mesh and no GUI. To add a new
kind of injected UI, add a callback to `CommandContext` and wire it once in the chat view — every
command can then trigger it; no per-command view code.

### Selections persist on `ThreadComposer`

The composer (`{user}/_Memex/ThreadComposer`, a data-bound `[MeshNode]`) is the **single source of
truth** for the thread's harness/agent/model. A pick command writes the selected node's **path** onto
a named composer field; the read-only status row above the input and the next submission read it
back. The command carries no state of its own — it names a *query* and a *field*.

---

## The common case: pick a mesh node (data, no code)

Most commands "pick a node by a query and drop it into the composer". Ship that as a
**`nodeType:Command` mesh node** — **no C# at all**. Its content is a `CommandDefinition`:

```jsonc
{ "$type": "CommandDefinition",
  "query": "namespace:Agent nodeType:Agent sort:order",  // what the picker lists + how it's ordered
  "composerField": "agentName",                          // camelCase ThreadComposer field to write
  "title": "Choose an agent" }
```

The slash word is the node's **id**, the help text its **description**. On execution the host builds a
`NodePickerRequest` from the definition and triggers `ShowNodePicker` — identical to a C# pick command.

**Ordering + eligibility live in the query, never in the GUI.** `sort:order` makes the picker's
default-to-first land on the catalog head (e.g. Assistant's `order: -1`). The picker renders the query
result as-is — it does not sort or filter. To change which nodes appear or their order, change the
**query**, not the view.

### The standard commands are mesh nodes

`/agent`, `/model`, `/harness` ship as Command nodes from `BuiltInCommandProvider` under the `Command`
partition (queries shown with `sort:order`). They are **imported into Postgres** on boot via
`CommandStaticRepoSource` (mirroring agents/models/Doc) — the distributed/Orleans routing never
consults the in-memory static adapter, so without the import `namespace:Command` queries return
nothing and **the chat finds no commands**. The same applies to the harness catalog
(`HarnessStaticRepoSource`). See [StaticRepoImport](/Doc/Architecture/StaticRepoImport).

> **`/agent` and `/model` resolve a PARTITION-AWARE registry query, not their stored one.** The picker
> host (`ThreadChatView.OpenPicker`) builds the agent list from the single canonical
> `AgentPickerProjection.BuildAgentQuery` — `namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent`
> — so a Space's or the user's own agents (placed in `{space}/Agent` / `{user}/Agent`) surface
> alongside the platform catalog (see [Extensible Defaults](/Doc/Architecture/ExtensibleDefaults)). It
> runs via `hub.GetQuery` on the portal hub, so per-user RLS hides another user's private agents. The
> same single query backs the chat combobox and the engine's agent selection (`AgentChatClient`, under
> the thread owner). The Command node's stored `query` is the declared fallback.

### Per-Space / per-NodeType / per-user commands

A Space or NodeType can define its **own** Command nodes in its partition. They are discovered through
**namespace inheritance** — `CommandNodeType.CommandQueries` unions the global catalog, the current
context node + ancestors, and the user's home + ancestors — so a command defined nearer the context
overrides a global one by id. Drop a `nodeType:Command` node under a Space and `/yourcommand` works in
that Space's chat, with zero code.

---

## A code pick command: `MeshNodePickCommand`

When you want the pick pattern in **code** (e.g. logic to build the query), subclass
`MeshNodePickCommand` — declare only the four things:

```csharp
public sealed class SpaceCommand : MeshNodePickCommand
{
    public override string Name => "space";
    public override string Description => "Pick a Space";
    protected override string Query => "nodeType:Space";       // add `sort:order` to order the picker
    protected override string ComposerField => "contextPath";
    protected override string Title => "Choose a Space";
}
```

Register it from your module's `ConfigureServices`:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatCommand, SpaceCommand>());
```

The base `Execute` normalises the argument (`@space/Acme` → `Acme`) and triggers
`context.ShowNodePicker`. `/space` now appears in autocomplete, pops the generic picker with no
argument, auto-selects an exact match with one (`/space Acme`), and writes the chosen path to
`contextPath`. No `CommandContext` field, no chat-view widget.

---

## A workflow command: implement `IChatCommand`

For anything beyond a pick, implement `IChatCommand` directly and drive the thread from the context.
`/help` is the canonical example — it reads `context.CommandRegistry` and triggers `ShowStatus`:

```csharp
public void Execute(CommandContext context)
{
    if (context.CommandRegistry is not { } registry)
    {
        context.ShowStatus?.Invoke("Command registry not available.", isError: true);
        return;
    }
    var help = BuildHelpText(registry);          // your workflow
    context.ShowStatus?.Invoke(help, isError: false);
}
```

A workflow command can write thread state too — `context.Hub.GetMeshNodeStream(context.ComposerPath)
.Update(...)` (compose + Subscribe, never await) — or trigger any callback the host exposes. The point
is the command owns the workflow; the host only provides the callbacks.

---

## How dispatch works

When the user runs `/name`, the chat view (`ThreadChatView.HandleSlashCommandAsync`) builds **one**
`CommandContext` with the GUI callbacks wired, then:

1. **registered `IChatCommand`?** (`/help`, your module's code commands) → `command.Execute(context)`.
2. **otherwise** → resolve a `nodeType:Command` **mesh node** by slash word (with namespace
   inheritance) and trigger the **same** `ShowNodePicker` from its `CommandDefinition`.

Both paths end at the same generic picker and write to the same `ThreadComposer`. The view contains
**no per-command code** — only the callback implementations.

---

## Executable example

`SpaceCommand` is exercised end-to-end in
`test/MeshWeaver.AI.Test/ChatCommandsTest.cs` → `CustomModulePickCommand_Works_WithZeroCoreChanges`:
it defines the command, registers it (so it surfaces in autocomplete), runs `Execute` with a captured
`ShowNodePicker`, and asserts the triggered `NodePickerRequest` — proving a module command works with
zero core changes. The handler/callback contract (null-safe headless host, `/help` → status, term
normalisation) is covered by the sibling tests in the same file; the import of the standard
command/harness catalog into Postgres is covered by `CommandHarnessImportSourceTest`.
