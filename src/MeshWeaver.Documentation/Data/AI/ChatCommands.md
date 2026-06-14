# Chat Slash-Commands

The chat input supports slash-commands (`/agent`, `/model`, `/harness`, `/help`, …). They are
**modular**: a module registers an `IChatCommand` and it immediately appears in the input's
autocomplete (via `ChatCommandRegistry` → `CommandAutocompleteProvider`) and executes — with **no
changes to the chat view or the command context**.

## The common case: pick a mesh node

Most commands "pick a node by a query and drop it into the composer". That pattern is the reusable
base **`MeshNodePickCommand`** — a concrete command declares only four things:

- **Name** — the slash word (`/space`).
- **Query** — the mesh query whose nodes the picker lists.
- **ComposerField** — the camelCase `ThreadComposer` field the selected node PATH is written to.
- **Title** — the picker header.

```csharp
public sealed class SpaceCommand : MeshNodePickCommand
{
    public override string Name => "space";
    public override string Description => "Pick a Space";
    protected override string Query => "nodeType:Space";
    protected override string ComposerField => "contextPath";
    protected override string Title => "Choose a Space";
}
```

Register it like any built-in command (from your module's `ConfigureServices`):

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatCommand, SpaceCommand>());
```

That is the whole change. `/space` now:

- appears in the chat input's slash-command autocomplete,
- with **no argument** pops the **generic node picker** listing every `nodeType:Space` node,
- with an **argument** (`/space Acme`) auto-selects an exact match — otherwise pre-filters the picker,
- writes the chosen node's **path** to the composer's `contextPath` on selection.

No `CommandContext` field, no chat-view widget, no per-command UI — the host renders ONE generic
picker for every node-pick command (`/agent`, `/model`, `/harness`, and yours).

## How it executes

`MeshNodePickCommand.ExecuteAsync` returns a `NodePickerRequest(Query, ComposerField, Title,
SearchTerm)`. The chat view's `OpenPicker`:

1. queries the mesh for `Query`,
2. auto-selects an exact `SearchTerm` match (so `/model gpt-4o` switches without a click) or shows the list,
3. writes the selected node's **path** to the composer field named by `ComposerField`.

The composer is the single source of truth for the thread's harness/agent/model selection (it is a
data-bound `[MeshNode]` picker), so the command never carries data of its own — it only names a
query and a field.

## Executable example

The `SpaceCommand` above is exercised end-to-end in
`test/MeshWeaver.AI.Test/ChatCommandsTest.cs` → `CustomModulePickCommand_Works_WithZeroCoreChanges`:
it defines `SpaceCommand`, registers it in a `ChatCommandRegistry`, and asserts it resolves (so it
surfaces in autocomplete) and returns the right `NodePickerRequest` — proving a module command works
with zero core changes.

## Commands that are not node-picks

For anything beyond the node-pick pattern, implement `IChatCommand` directly. `ExecuteAsync` gets a
`CommandContext` (the parsed command, the `Hub`, the current `ContextPath`, the command registry)
and returns a `CommandResult` — `CommandResult.Ok(message)` / `CommandResult.Error(message)` for a
text response, or `CommandResult.ShowPicker(...)` to pop the generic picker. `/help` is one such
command.
