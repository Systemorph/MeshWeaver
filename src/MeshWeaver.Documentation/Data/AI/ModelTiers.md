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

### How Auto dispatches — two stages: a floor, then a refinement

Auto resolves in two stages, and the whole design turns on this: the first **always** produces a runnable answer with no network call, and the second only ever *improves* it.

**1. The floor — no model call.** `AgentChatClient.ApplyStaleModelFallback` runs synchronously, before any factory sees the selection, and dispatches on the tier the selected agent declares — through the same chain as everything else (label → deprecated config → default):

- The agent declares a tier → that tier's model.
- No tier, or no agent selected and the default agent declares none → the deployment default.
- Nothing usable at all → the round fails audibly. The floor never resolves to nothing, and never to Auto.

So a round can always start, on a predictable model, without a round-trip.

**2. The refinement — one cheap classification call.** Then `ApplyAutoRouterSelectionAsync` (#1951 — *"Auto should trigger an agent to determine the most appropriate model for the thread"*) makes **one** bounded call over the round's actual content and asks it to pick the best candidate. It is deliberately cheap: `AutoModelRouter` caps the task text at 2000 chars and the call at `MaxOutputTokens: 120`, `Temperature: 0`, under a hard timeout. If it names a better model, the round switches to it.

**Predictability is preserved by construction.** Any failure of the refinement — a timeout, an unparseable answer, an id outside the candidate list, an engine with no bare chat client — silently **keeps the floor**. The worst case is exactly the floor you would have gotten anyway, and the choice is logged with the model's own stated reason. This is what makes it safe to let a second model decide: it can only pick *within* the usable candidates, and it can never leave you worse than the fixed default.

Whichever stage decides, the **resolved model is stamped onto the round** (`SubstitutedFromModel` → the response cell), so a thread on Auto reports — and bills — the model that really answered.

> 🕰️ This supersedes the earlier "Auto is a fixed default, no classification call" rule. The floor *is* that fixed default; the refinement (#1951) was added on top so Auto can route a coding-shaped ask to the `coding` tier even when the agent declared no tier — without ever risking a round on an unpredictable pick.

## Auto and the tiers route to open-weight models only

A deliberate policy on the Systemorph deployments (2026-08): **Auto — and therefore every tier it routes to — selects only open-weight models** (GLM, Kimi, Qwen, DeepSeek). Proprietary models (Claude, GPT, Gemini, Grok) stay installed and fully usable, but they are **untiered**: a person can pick one by hand in the composer and an explicit pick always wins, yet Auto never routes to one on its own.

Why draw the line there: open weights are cheap and fast, and — the point that makes this more than a cost lever — **they can run on-device.** The same `chat` tier that OpenRouter serves in the cloud is served by a local Ollama on a laptop (a `Provider/OpenAICompatible` node at `localhost`), so the highest-traffic tier runs free, private, and offline. A proprietary model cannot do that, which is exactly why it belongs behind a manual choice rather than an automatic one.

**How it's expressed:** put open-weight tier labels on the open-weight nodes, and leave the proprietary nodes untiered. Every automatic rung already skips a router and any model whose credentials don't resolve; keep the tier labels only on open-weight nodes and Auto lands on open weights. *(The airtight guarantee — filtering the refinement's candidate list to open weights and running the classifier itself on a small open-weight model — is a router change being finalised separately; the untiered-proprietary + open-weight-labels arrangement already holds for every ordinary round.)*

**The current map** — both cloud instances, via OpenRouter; `utility` and `reasoning` are left unlabelled so they fall through to the default (`glm-5.3`):

| Tier | Model | Note |
|---|---|---|
| `utility` | `z-ai/glm-5.3` *(via default)* | cheap background jobs |
| `chat` | `qwen/qwen3.6-35b-a3b` | the everyday round — most traffic — a fast MoE (~3B active) |
| `reasoning` | `z-ai/glm-5.3` *(via default)* | multi-step analysis, planning |
| `coding` | `moonshotai/kimi-k3` | the strongest rung |
| *manual only* | Claude · GPT · Gemini · Grok | installed, **untiered** — a human picks them |

On a laptop the same four tiers point at a local qwen instead of OpenRouter — same labels, a different provider node per mesh.

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

## Related

- [Setting Up Model Providers](/Doc/AI/ModelProviderSetup) — where models come from (config-seeded nodes), where the key lives, and the open-weight model choice per tier
- [AI Model Provider Settings](/Doc/AI/ModelProviderSettings) — the Settings → Models UI (Providers · Models · Model Tiers)
- [AI Provider Configuration](/Doc/AI/ProviderConfiguration) — credential/endpoint wiring and model-to-factory routing
