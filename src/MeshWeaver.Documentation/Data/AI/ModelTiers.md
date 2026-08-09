---
nodeType: Markdown
name: Model Tiers
category: AI
description: Agents ask for a usage tier — utility, chat, reasoning, coding — and each model node carries the tier it satisfies. Auto is the default for a new thread and dispatches on the agent's tier.
icon: /static/NodeTypeIcons/task-list.svg
---

An agent should say **what kind of work it is doing**, not which model it needs. A model id is a deployment detail — it changes when a provider ships a new version, when prices move, when you switch clouds — and an agent that names one hard-codes that detail into its definition.

So agents ask for a **tier**, and each model NODE carries the tier it satisfies. Setting up a new environment is then one job: **label your models**. No `ModelTier:*` environment variables, no per-deployment config drift — the labels are data on the mesh, visible and editable like any other content.

## Named for the job, not for the size

The rungs used to be `S` / `M` / `L` / `XL`. That was the wrong axis twice over. A size says nothing about *when* to pick one — you still have to know the house convention to read `L` at a call site. And sizes age: this year's large is next year's medium, so every label drifts while the work it was chosen for stays exactly the same. **Coding is still coding.**

| Tier | What belongs here | Rank |
|---|---|---|
| **`utility`** | Cheap background micro-jobs the user never waits for: thread auto-naming, node icons and descriptions, classification, triage. Latency and cost dominate. | 0 |
| **`chat`** | Ordinary interactive conversation — the everyday round. Fast, and good enough to hold a discussion and call tools. | 10 |
| **`reasoning`** | Multi-step analysis, planning, synthesis — the hard asks that are **not** code. Chosen when the answer has to be thought through. | 20 |
| **`coding`** | Writing and changing code — the strongest model the deployment has. Code is where a weaker model costs the most: a wrong patch is worse than a slow one. | 30 |

`coding` is the top rung deliberately. It is the one job where a cheaper model does not merely answer *less well* — it produces work that looks finished and is wrong, and the cost of that lands downstream, on a human, later.

The legacy vocabulary keeps working everywhere a label is accepted, carried as **aliases** on the tiers: `utility` → **utility**, `light` → **chat**, `standard` → **reasoning**, `heavy` → **coding**, and the short-lived `S`/`M`/`L`/`XL` size labels map onto the same four by rank. An existing agent carrying `modelTier: standard` needs no change.

> A model whose name contains "coder" is not automatically a `coding` model. Benchmark before you label: in one head-to-head, the second-cheapest "coder" model failed four of five checks on a routine C# task while a cheaper general model passed all five. **Label from measurements, not from model names.**

## Tiers are nodes, so you can change them

The four above are what MeshWeaver **ships**, not what it hard-codes. Each is an ordinary mesh node of `nodeType:ModelTier` under `Provider/Tier`, listed in the AI menu beside Models and Providers — because it is the third thing you configure when you set a deployment up: which models exist, what each one is FOR, and whose key pays for them.

Rename `reasoning`, re-rank the set, rewrite a purpose, add a `vision` rung — no code change, no config key, no redeploy. The seeding is **create-if-absent** (`SyncBehavior.ExcludeThisAndChildren`), so your edits survive every redeploy, exactly like an admin's provider key does.

```json
{ "nodeType": "ModelTier",
  "content": { "$type": "ModelTierDefinition", "id": "vision", "displayName": "Vision",
               "purpose": "Rounds that have to look at an image.", "rank": 15 } }
```

Deleting every tier node is survivable by design: resolution falls back to the shipped four, so it never depends on a node being there.

## Setting up a new environment

Two steps. Neither is required to be complete.

**1. Pin the default.** Exactly one model gets `order: -1`. The default is simply the lowest-`order` model whose credentials actually work, so `-1` means "this one, unless somebody picks otherwise".

```json
{ "nodeType": "LanguageModel",
  "content": { "$type": "ModelDefinition", "id": "z-ai/glm-5.2",
               "providerRef": "Provider/OpenRouter", "order": -1, "tier": "reasoning" } }
```

**2. Label whatever else you have.**

```json
{ "content": { "$type": "ModelDefinition", "id": "moonshotai/kimi-k3", "order": 7, "tier": "coding" } }
```

> ⚠️ Without a deliberate `-1`, the default is whatever happens to sort first. On one deployment that silently made a slow, expensive reasoning model the default for every round where nobody picked a model — for weeks, invisibly, because nothing is wrong with such a round except the bill and the latency.

### You do NOT have to populate every tier

**A tier nobody carries is a miss, and a miss falls through to the default.** An environment with a single labelled model is valid and works: agents asking for `utility`, `chat` or `coding` all land on the default rather than failing. Populate the rungs you actually have, and add more as you add models.

That is the difference from the old `ModelTier:*` config: a missing tier there meant a missing environment variable, discovered when a background agent quietly ran on the wrong model. Here the fallback is one rule, in one place, and it is the same rule everywhere.

## Resolution never fails — it degrades

For one round, in order — the first hit wins:

1. **The user's explicit pick** in the composer (anything other than Auto). Always wins; a tier label never overrides a human choice.
2. **The tier label on a model node** — the lowest-`order` model carrying the tier the agent declared.
3. **The deprecated `ModelTier:*` config**, and only when it names a model whose credentials actually resolve.
4. **The deployment default** — lowest `order`, credentials verified.

Two exclusions apply to every automatic step (2–4):

- **Models whose credentials don't resolve are skipped**, so a fallback never lands on another broken model.
- **Routers are skipped** — see Auto, below.

There is exactly **one** outcome with no model: an empty or entirely-unusable catalog. That is reported as `ModelTierSource.None`, which the round turns into a speaking "no AI model available" failure — never a silent wrong model. Every other shape — an unknown label, an unpopulated tier, no tier at all, no labels anywhere, every tier node deleted — costs a rung, never a round.

The rules are pure functions over the catalog (`ModelTierCatalog`), so "which model does an agent asking for `coding` get" is answerable from data alone, without a running mesh. They are unit-tested that way, including every stripped-catalog shape above.

### Where the label actually takes effect

Worth knowing, because it is not where you would guess. When a round starts with **no usable model selected** — or with **Auto** selected — `AgentChatClient.ApplyStaleModelFallback` resolves one before any factory is chosen, and that is where the tier is honoured.

It has to happen there. The concrete factories also consult the tier (`ResolveTierModel`), but only when no model is selected — and by that point the resolution has already set one. Resolving the bare default instead would make **every** tiered agent run on the default, with the label sitting on the node looking meaningful and changing nothing. A tier label that silently does nothing is worse than no label at all, because you stop looking.

An explicit, usable composer selection never reaches it at all — the method returns early when the current model resolves — so **a human pick always wins over a label**.

### A fallback is never silent to an operator

The user sees nothing: an unusable pinned model is a config problem they cannot act on mid-thread. The **operator** always can:

- the round records the model that **actually served** (`update.ModelId` → the response cell), so token usage and per-model roll-ups are keyed by what really ran, not by what was asked for;
- `SubstitutedFromModel` is stamped on the round's response cell, so a non-interactive round carries the fact in its data;
- and a warning names both models — including the one that matters most, *"no usable model carries tier `coding`, dispatched to the deployment default"*, which is the difference between "my coding agent runs on the coding model" and "…runs on whatever sorts first".

### "My agent declares `coding` but ran on the default"

That is the designed fallback, not a bug, and there are exactly three causes:

1. **No model carries the `coding` label** — the miss falls through. Check `tier` on your model nodes; the warning above names this case explicitly.
2. **The labelled model's credentials don't resolve** — it is skipped like any unusable model. Check its `providerRef` and that the provider node has a key.
3. **Someone picked a concrete model in the composer** — an explicit selection wins, by design.

## Auto — the default for a new thread

**Auto** is not a model, it is a *router*: it holds no endpoint and no key, and it never serves a round. It is a `LanguageModel` node like any other, marked on its content:

```json
{ "content": { "$type": "ModelDefinition", "id": "auto", "isRouter": true, "order": -10 } }
```

`order: -10` sorts it ahead of even a deliberately-pinned `-1`, which is what makes it the **default selection for a new thread**. `isRouter: true` keeps it out of every *automatic* rung — the tier lookup and the deployment default — because a rung that could return Auto would resolve Auto to Auto, or hand the round to something that dispatches rather than answers.

### Its dispatch rule

**Auto runs the tier the selected agent declares.** That is the whole rule.

- The agent declares a tier → Auto dispatches to that tier, through the same chain as everything else (label → deprecated config → default).
- The agent declares none, or no agent is selected and the default agent declares none → the deployment default.
- Nothing usable at all → the round fails audibly. Auto never resolves to nothing, and never to Auto.

No classification call, no second model deciding what the first should be. That is deliberate: **a router nobody can predict is worse than a fixed default.** The tiers are already assigned to agents, so dispatching on the agent's tier is the reading of "Auto" that is consistent with the rest of the system — and it costs nothing, because there is no extra round-trip to make the decision.

The dispatch happens before any factory sees the selection, and the resolved model is stamped onto the round, so a thread on Auto reports the model that really answered.

## Migrating off the `ModelTier:*` config

The old keys — `ModelTier__Heavy`, `ModelTier__Standard`, `ModelTier__Light`, `ModelTier__Utility` — are **still read**, so a deployment that sets them does not lose its mapping on upgrade. They map onto the tiers by rank, carried by each tier's aliases:

| Deprecated key | Tier |
|---|---|
| `ModelTier__Heavy` | `coding` |
| `ModelTier__Standard` | `reasoning` |
| `ModelTier__Light` | `chat` |
| `ModelTier__Utility` | `utility` |

They sit **below** the node labels: a labelled model always wins, and the config only ever answers a tier no node carries — and even then, only when it names a model whose credentials resolve. Because the mapping goes through the aliases rather than a hard-coded switch, it keeps working for a deployment that renamed a tier, and an operator can point a *new* tier at an old key just by adding the alias.

**To retire them:** put a `tier` on your model nodes, confirm the resolution warnings stop naming `LegacyConfig`, and delete the keys.
