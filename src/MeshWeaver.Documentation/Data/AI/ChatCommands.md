# Chat Slash-Skills

The chat input supports slash-skills (`/agent`, `/model`, `/harness`, …). A **skill is "a thing that
does something"** when the user invokes it — and this is the *one* unified concept: what we used to call
slash-*commands* are skills. A skill's "doing" can be:

- **open a combobox and select** a node (an agent / model / harness) → write the pick to the composer;
- **load a document/manual into the content window**;
- **connect / log in** a CLI harness;
- **inject instructions** — a `SKILL.md` body mounted to the Claude Code / Copilot CLIs and advertised to
  the MeshWeaver agent to load on demand.

> **Skills are not agents.** *Agents == system prompts* (a persona the model runs as — including the
> utility agents: naming, summary, NodeInitializer, DescriptionWriter). *Skills == capabilities loaded as
> you go.* An agent is never a skill, and agents are not mounted to disk.

A skill is a **declarative mesh node** (`nodeType:Skill`) — there is **no C# handler class** and no GUI
code per skill. A module, Space, NodeType or user ships a `Skill` node and it just works in the chat,
discovered through namespace inheritance.

---

## The skill node

A skill's content is a `SkillDefinition`. The slash word is the node's **id**, the display name + help
text are the node's **name** + **description**:

```csharp
public record SkillDefinition
{
    public string? Instructions { get; init; }   // SKILL.md body — CLI harnesses + agent load-on-demand
    public SkillAction? Action { get; init; }     // what it DOES in the chat (null for a pure instruction skill)
    public bool AutoMount { get; init; } = true;  // mount to the shared skills dir / advertise to the agent
    public bool LaunchesSubThread { get; init; } = false; // run in a sub-thread vs inline
}

public record SkillAction
{
    public required SkillActionKind Kind { get; init; }   // Pick | OpenContent | Connect | Disconnect
    public string? Query { get; init; }        // Pick: the combobox query (+ `sort:order`)
    public string? Field { get; init; }        // Pick: camelCase ThreadComposer field (harness|agentName|modelName)
    public string? Title { get; init; }        // Pick: combobox title
    public string? ContentPath { get; init; }  // OpenContent: node/path to load into the content window
    public string? Provider { get; init; }     // Connect/Disconnect: ClaudeCode | Copilot
}
```

A skill is a **behaviour** (`Action`) and/or an **instruction** (`Instructions`). A pure-instruction skill
has no `Action` (it does nothing in the chat — it is mounted to the CLIs / advertised to the agent); a
pure-behaviour skill has no `Instructions`.

### Selections persist on `ThreadComposer`

The composer (a data-bound `[MeshNode]`) is the **single source of truth** for the thread's
harness/agent/model. A `Pick` skill writes the selected node's **path** onto a named composer field; the
read-only status row above the input and the next submission read it back. The skill carries no state of
its own — its `Action` names a *query* and a *field*.

---

## The common case: pick a mesh node (`Pick`)

Most skills "pick a node by a query and drop it into the composer". That is a `Pick` action — **no C# at
all**:

```jsonc
{ "$type": "SkillDefinition",
  "action": {
    "kind": "Pick",
    "query": "namespace:Agent nodeType:Agent sort:order",  // what the picker lists + how it's ordered
    "field": "agentName",                                  // camelCase ThreadComposer field to write
    "title": "Choose an agent" } }
```

On execution the chat host builds a `NodePickerRequest` from the action and pops the generic node
selector; selecting a node writes its path to the composer field. This *is* "open a combobox and select
an agent".

**Ordering + eligibility live in the query, never in the GUI.** `sort:order` makes the picker's
default-to-first land on the catalog head (e.g. Assistant's `order: -1`). The picker renders the query
result as-is — to change which nodes appear or their order, change the **query**, not the view.

### The standard skills are mesh nodes

`/agent`, `/model`, `/harness` ship as `Pick` skill nodes from `BuiltInSkillProvider` under the `Skill`
partition. They are **imported into Postgres** on boot via `SkillStaticRepoSource` (mirroring
agents/models/Doc) — the distributed/Orleans routing never consults the in-memory static adapter, so
without the import `namespace:Skill` queries return nothing and **the chat finds no skills**. The same
applies to the harness catalog (`HarnessStaticRepoSource`). See
[StaticRepoImport](/Doc/Architecture/StaticRepoImport).

> **`/agent` and `/model` resolve a PARTITION-AWARE registry query, not their stored one.** The picker
> host (`ThreadChatView.OpenPicker`) builds the agent list from the single canonical
> `AgentPickerProjection.BuildAgentQueries` — `namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent`
> — so a Space's or the user's own agents surface alongside the platform catalog. It runs via
> `hub.GetQuery`, so per-user RLS hides another user's private agents. The Skill node's stored `query` is
> the declared fallback.

### Per-Space / per-NodeType / per-user skills

A Space or NodeType can define its **own** Skill nodes in its partition. They are discovered through
**namespace inheritance** — `SkillNodeType.SkillQueries` unions the global catalog, the current context
node + ancestors, and the user's home + ancestors — so a skill defined nearer the context overrides a
global one by id. Drop a `nodeType:Skill` node under a Space and `/yourskill` works in that Space's chat,
with zero code.

---

## Other behaviours

- **`OpenContent`** — load a node/manual into the content window (side panel) via the navigation bridge
  (`SidePanelState.SetContentPath`). Set `action.contentPath`, or pair a `Pick` with the content window.
- **`Connect` / `Disconnect`** — harness auth. Under a non-MeshWeaver harness, `/login` and `/logout` are
  owned by the harness (`IHarness.Commands`) and route to its connect flow; they take priority in dispatch.
- **`Instructions`** (no `Action`) — a pure-instruction skill. With `AutoMount`, `AgentSkillSyncService`
  materialises it to `{workspace}/.claude/skills/<slug>/SKILL.md` so Claude Code / Copilot discover it,
  and the MeshWeaver agent advertises it (name + description) to load on demand via the `load_skill` tool.

---

## How dispatch works

When the user runs `/name`, the chat view (`ThreadChatView.HandleSlashCommandAsync`):

1. **harness-owned command?** (`/login`, `/logout` under a non-MeshWeaver harness) → route to the harness.
2. **otherwise** → resolve a `nodeType:Skill` **mesh node** by slash word (with namespace inheritance,
   `ResolveSkillNodeAndRun`) and run its `Action` — `Pick` pops the combobox, `OpenContent` loads the
   content window. Pure-instruction skills have no chat behaviour.

The view contains **no per-skill code** — only the generic picker + content-window callbacks. All pick
skills write to the same `ThreadComposer`.

---

## Tests

- `test/MeshWeaver.AI.Test/SkillNodeTypeTest.cs` — the POCO data layer: `BuiltInSkillProvider` ships the
  three `Pick` skills, `SkillQueries` (inheritance), `ProjectSkills` (dedupe + JsonElement),
  `SkillInfo.ToPickerRequest`.
- `test/MeshWeaver.AI.Test/SkillAutocompleteTest.cs` — typing `/` lists the built-in skills.
- `test/MeshWeaver.AI.Test/SkillHarnessImportSourceTest.cs` — the Skill + Harness catalogs import into
  Postgres (and the sync gate drops the in-memory surface on the DB-synced path).
