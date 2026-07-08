# Chat Slash-Skills

The chat input supports slash-skills (`/agent`, `/model`, `/harness`, …). A **skill is "a thing that
does something"** when the user invokes it — and this is the *one* unified concept: what we used to call
slash-*commands* are skills. A skill's "doing" can be:

- **open a combobox and select** a node (an agent / model / harness) → write the pick to the composer;
- **load a document/manual into the content window**;
- **navigate the UI to a node/doc/page** — pane-aware and resilient (`/navigate`, see below);
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
    public bool AutoMount { get; init; } = true;  // advertise the skill to the agent up-front (vs load-on-demand only)
    public bool LaunchesSubThread { get; init; } = false; // run in a sub-thread vs inline
}

public record SkillAction
{
    public required SkillActionKind Kind { get; init; }   // Pick | OpenContent | Navigate | Connect | Disconnect | NewThread
    public string? Query { get; init; }        // Pick: the combobox query (+ `sort:order`)
    public string? Field { get; init; }        // Pick: camelCase ThreadComposer field (harness|agentName|modelName)
    public string? Title { get; init; }        // Pick: combobox title
    public string? ContentPath { get; init; }  // OpenContent / Navigate: node/path (Navigate: optional fixed target)
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

### The standard skills are authored as `.md` files

`/agent`, `/model`, `/harness` ship as `Pick` skill nodes, authored the **same way as agents**: a markdown
file with a YAML frontmatter header (`src/MeshWeaver.AI/Data/Skill/*.md` — e.g. `agent.md` → `/agent`),
loaded by `BuiltInSkillProvider`. A behaviour skill puts its `action:` block in the frontmatter; an
instruction skill puts its how-to in the markdown body:

```yaml
---
nodeType: Skill
name: /agent
description: Switch the agent for subsequent messages
action: { kind: Pick, query: "namespace:Agent nodeType:Agent sort:order", field: agentName, title: Choose an agent }
---
```

They are **imported into Postgres** on boot via `SkillStaticRepoSource` (mirroring agents/models/Doc) — the
distributed/Orleans routing never consults the in-memory static adapter, so without the import
`namespace:Skill` queries return nothing. See [StaticRepoImport](/Doc/Architecture/StaticRepoImport).

### Discovery is the unified registry pattern — same as agents and models

Skills, agents, and models are discovered the **identical** way: a per-partition registry query unioning
the platform namespace, the current space's, and the user's own —
`namespace:{user}/Skill|{space}/Skill|Skill nodeType:Skill` (`AgentPickerProjection.BuildSkillQueries`, the
sibling of `BuildAgentQueries` / `BuildModelQueries`). All are **public-read top-level domains**. So a
Space's own skills (under `{space}/Skill`) or the user's (`{user}/Skill`) surface alongside the platform
catalog, with per-user RLS hiding another user's private skills. Drop a `nodeType:Skill` node under
`{space}/Skill` and `/yourskill` works in that Space's chat, with zero code.

### Agents proactively offer to create skills

A conversational agent watches for **repetition** — the user asking for the same multi-step task more
than once — and **proactively offers** to capture it as a `/<name>` skill (it does not wait to be asked).
"Create a skill" means `create` a `nodeType:Skill` node with a `SkillDefinition` content (an
`Instructions` how-to and/or an `Action`), placed under the **user's own** `{user}/Skill` (private to
them) or the **Space's** `{space}/Skill` (shared with that Space) — the platform-wide catalog is `Skill`.
This guidance lives in the shared agent base prompt (`AgentChatClient`); the agent reads the
`SkillDefinition` shape from this page.

---

## Other behaviours

- **`OpenContent`** — load a node/manual into the content window (side panel) via the navigation bridge
  (`SidePanelState.SetContentPath`). Set `action.contentPath`, or pair a `Pick` with the content window.
- **`Navigate`** — **take me there.** Navigate the UI to a node, doc, or page. Ships as the built-in
  **`/navigate`** skill (`src/MeshWeaver.AI/Data/Skill/navigate.md`). Two properties make it robust:
  - **Pane-aware** — the target opens in the pane *opposite* the thread, so the conversation and where it
    sent you sit side by side (the same rule as the context chip, `OnContextChipClicked`):
    a thread in the **main pane** opens the target in the **side panel**; a thread in the **side panel**
    **changes the URL and navigates the main pane**.
  - **Resilient** (`NavigationResolver` + the pure, unit-tested `NavigationTargetResolver`) — a **single
    path-shaped argument** is resolved as a **direct path first** (with URL correction: a leading `@`, a
    stray `/node/` segment, a pasted `https://host/…` prefix, doubled slashes and percent-encoding are all
    cleaned up), and if the exact node isn't there it **falls back to the best search match** rather than
    dead-ending; **free text** (`/navigate model settings`) is made sense of by matching an
    **intent → skill** first (so "change my model" runs **`/model`** — a skill *does* more than a route
    change), otherwise the best-matching node. It **never claims to open a place that doesn't exist**.

  The agent's server-side `NavigateTo` tool routes through the same resolver, so "take me there" resolves
  honestly no matter who asks. Tested by `NavigationTargetResolverTest` (the pure algorithm) and
  `MeshPluginTest` (resilient + honest resolution against the live mesh).
- **`NewThread`** — **start a fresh chat.** Ships as the built-in **`/clear`** skill
  (`src/MeshWeaver.AI/Data/Skill/clear.md`). It REPLACES whatever is showing in the **side panel** with an
  empty new-chat composer and **leaves the main pane alone** — the same target as the "+" button
  (`SidePanelState.SetContentPath(null)` drops the open thread so `ThreadSidePanelContent` swaps in the
  composer; the panel is opened if it was closed). No navigation. Handled by `ThreadChatView.RunSkill`.
- **`Connect` / `Disconnect`** — harness auth. Under a non-MeshWeaver harness, `/login` and `/logout` are
  owned by the harness (`IHarness.Commands`) and route to its connect flow; they take priority in dispatch.
- **`Instructions`** (no `Action`) — a pure-instruction skill (its how-to is the markdown body). Skills are
  **read from the mesh on demand**, never materialised to disk: the MeshWeaver agent finds them with
  `search nodeType:Skill` and injects one with the `load_skill` tool; the CLI harnesses (Claude Code /
  Copilot) discover + read them through the `meshweaver` MCP server. A `LaunchesSubThread` skill runs in
  its own sub-thread when loaded (the generic `StartThread` launcher).

### Using an instruction skill: `/code <task>`

An instruction skill is invoked with the task typed **after the slash word** — everything after the
skill name is the work order:

```
/code build a Todo NodeType with a Kanban board layout area
```

The chat digests the trailing text into a normal round (`SkillInfo.ToSubmissionText`): the typed task
is submitted prefixed with a `load_skill` directive, so the agent loads the skill's instructions (the
`SKILL.md` body) first and then applies them to exactly what you typed. `/code` alone (no task) just
shows the skill's help text — there is nothing to run. Under a CLI harness (Claude Code / Copilot) the
raw `/code …` text is instead forwarded 1:1 to the harness, which resolves the skill itself through
the `meshweaver` MCP server.

---

## How dispatch works

When the user runs `/name`, the chat view (`ThreadChatView.HandleSlashCommandAsync`):

1. **harness-owned command?** (`/login`, `/logout` under a non-MeshWeaver harness) → route to the harness.
2. **otherwise** → resolve a `nodeType:Skill` **mesh node** by slash word (with namespace inheritance,
   `ResolveSkillNodeAndRun`) and run its `Action` — `Pick` pops the combobox, `OpenContent` loads the
   content window, `Navigate` resolves the target and opens it pane-aware, `NewThread` (`/clear`) replaces
   the side panel with a fresh new-chat composer. A pure-instruction skill with trailing text submits the
   task as a round with a `load_skill` directive (see above); without trailing text it shows the skill's help.

The view contains **no per-skill code** — only the generic picker + content-window callbacks. All pick
skills write to the same `ThreadComposer`.

---

## Tests

- `test/MeshWeaver.AI.Test/SkillNodeTypeTest.cs` — the POCO data layer: `BuiltInSkillProvider` ships the
  three `Pick` skills, `SkillQueries` (inheritance), `ProjectSkills` (dedupe + JsonElement),
  `SkillInfo.ToPickerRequest`, and `SkillInfo.ToSubmissionText` (an instruction skill digests the text
  typed after the slash word into a `load_skill`-prefixed round).
- `test/MeshWeaver.AI.Test/SkillAutocompleteTest.cs` — typing `/` lists the built-in skills.
- `test/MeshWeaver.AI.Test/SkillHarnessImportSourceTest.cs` — the Skill + Harness catalogs import into
  Postgres (and the sync gate drops the in-memory surface on the DB-synced path).
