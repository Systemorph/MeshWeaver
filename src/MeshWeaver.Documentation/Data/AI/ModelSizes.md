---
nodeType: Markdown
name: Model Sizes (S / M / L / XL)
category: AI
description: Label your models by size on the node, pin one default at order -1, and agents pick a rung instead of a model id — the whole model-selection setup for a new environment.
icon: /static/NodeTypeIcons/document.svg
---

# Model Sizes (S / M / L / XL)

An agent should say **how much model it needs**, not which model it needs. A model id is a deployment detail — it changes when a provider ships a new version, when prices move, when you switch clouds — and an agent that names one hard-codes that detail into its definition.

So agents ask for a **size**, and each model NODE carries the size label it satisfies. Setting up a new environment is then one job: **label your models**. No `ModelTier:*` environment variables, no per-deployment config drift — the labels are data on the mesh, visible and editable like any other content.

## The four rungs

| Label | What belongs here | Chosen for |
|---|---|---|
| **S** | Cheapest and fastest, no code involved | Classification, thread titles, summaries, the Auto router's own decision |
| **M** | Fast general-purpose that can still write correct code | Ordinary chat and agent rounds |
| **L** | The capable default | Complex reasoning, multi-step agent work, code that matters |
| **XL** | The strongest available — slowest, most expensive | The hardest asks, chosen deliberately |

The legacy tier vocabulary still works everywhere a label is accepted: `utility` → **S**, `light` → **M**, `standard` → **L**, `heavy` → **XL**. Existing agents carrying `modelTier: utility` need no change.

> A model whose name contains "coder" is not automatically an M. Benchmark before you label: in one head-to-head, the second-cheapest "coder" model failed four of five checks on a routine C# task while a cheaper general model passed all five. **Label from measurements, not from model names.**

## Setting up a new environment

Two steps. Neither is required to be complete.

**1. Pin the default.** Exactly one model gets `order: -1`. The default is simply the lowest-`order` model whose credentials actually work, so `-1` means "this one, unless somebody picks otherwise".

```json
{ "nodeType": "LanguageModel",
  "content": { "$type": "ModelDefinition", "id": "z-ai/glm-5.2",
               "providerRef": "Provider/OpenRouter", "order": -1, "size": "L" } }
```

**2. Label whatever else you have.**

```json
{ "content": { "$type": "ModelDefinition", "id": "moonshotai/kimi-k3", "order": 7, "size": "XL" } }
```

> ⚠️ Without a deliberate `-1`, the default is whatever happens to sort first. On one deployment that silently made a slow, expensive reasoning model the default for every round where nobody picked a model — for weeks, invisibly, because nothing is wrong with such a round except the bill and the latency.

### You do NOT have to populate every size

**A size nobody carries is a miss, and a miss falls through to the default.** An environment with a single labelled model is valid and works: agents asking for S, M or XL all land on the default rather than failing. Populate the rungs you actually have, and add more as you add models.

That is the difference from the old `ModelTier:*` config: a missing tier there meant a missing environment variable, discovered when a background agent quietly ran on the wrong model. Here the fallback is one rule, in one place, and it is the same rule everywhere.

## How resolution actually runs

For one round, in order — the first hit wins:

1. **The user's explicit pick** in the composer. Always wins; a size label never overrides a human choice.
2. **The agent's declared size** (`AgentConfiguration.ModelTier`) → the lowest-`order` model carrying that label.
3. **Legacy `ModelTier:*` config**, for deployments still expressing tiers that way.
4. **The deployment default** — lowest `order`, credentials verified.

Two exclusions apply to every automatic step (2–4):

- **Models whose credentials don't resolve are skipped**, so a fallback never lands on another broken model.
- **Routers are skipped** — see below.

The rules are pure functions over the catalog (`ModelSizeCatalog`), so "which model does an agent asking for L get" is answerable from data alone, without a running mesh. They are unit-tested that way.

### Where the label actually takes effect

Worth knowing, because it is not where you would guess. When a round starts with **no usable model selected**, `AgentChatClient.ApplyStaleModelFallback` **seeds** one before any factory is chosen — and that seed is where the size is honoured (`ResolveModelIdForSize(agent.ModelTier)`, which is the plain default when the agent declares nothing).

It has to happen there. The concrete factories also consult the tier (`ResolveTierModel`), but only when no model is selected — and by that point the seed has already set one. Seeding the bare default instead would make **every** sized agent run on the default, with the label sitting on the node looking meaningful and changing nothing. A size label that silently does nothing is worse than no label at all, because you stop looking.

An explicit, usable composer selection never reaches the seed at all — the fallback returns early when the current model resolves — so **a human pick always wins over a label**.

### "My agent declares L but ran on the default"

That is the designed fallback, not a bug, and there are exactly three causes:

1. **No model carries the `L` label** — the miss falls through. Check `size` on your model nodes.
2. **The labelled model's credentials don't resolve** — it is skipped like any unusable model. Check its `providerRef` and that the provider node has a key.
3. **Someone picked a model in the composer** — an explicit selection wins, by design.

The operator log line names the swap; the round itself is silent, deliberately (an unusable pinned model is a config problem a user cannot act on mid-thread).

## Auto, and why routers are excluded

The **Auto** entry is not a model — it is a *router*: it looks at the ask, estimates how much model it needs, and dispatches to a real one. It is marked on its node:

```json
{ "content": { "$type": "ModelDefinition", "id": "openrouter/auto", "isRouter": true, "order": -10 } }
```

`isRouter: true` keeps it out of every automatic selection — the size lookup and the default. That exclusion is not cosmetic: a default that could resolve to Auto would resolve Auto to Auto, and a size that could resolve to Auto would hand the round to something that dispatches rather than answers. **Auto is reachable only by an explicit pick** (which is why it can still sort first in the picker at a low `order`).

Its cost is worth stating plainly: routing adds a classification call before the real one, and it will sometimes route wrong. Anything touching code should land on L or XL; the router's own decision runs on S, where a wrong guess costs a fraction of a cent.
