---
Name: Models are picked by what they are for — and Auto is the new default
Category: Feature
Description: Model tiers are now named for the job (utility, chat, reasoning, coding), live as editable nodes in the AI menu, and Auto is the default for a new thread and actually dispatches.
Icon: Sparkle
Order: -20260809
---

# Models are picked by what they are for — and Auto is the new default

Choosing a model used to mean knowing which one your deployment considers "large".
Tiers were labelled `S` / `M` / `L` / `XL`, which told you the size and never the
purpose — and sizes age, so this year's large quietly became next year's medium
while the work stayed the same.

Tiers are now named for the **job**:

- **Utility** — cheap background micro-jobs you never wait for: naming a thread,
  writing an icon or a description, triage.
- **Chat** — the everyday round.
- **Reasoning** — multi-step analysis and planning, when the answer has to be
  thought through.
- **Coding** — writing and changing code, on the strongest model you have.
  Code is where a weaker model costs the most: a wrong patch is worse than a slow one.

They are ordinary nodes now, listed under **Tiers** in the AI menu next to Models
and Providers — so you can rename one, re-rank them, rewrite what a tier is for,
or add your own, and the edit survives every update.

**Auto** is the default selection for a new thread, and it now does something: it
runs the tier the selected agent asked for, and your deployment's default when the
agent asked for nothing. No extra call, no guessing — an agent that writes code
lands on your coding model without anyone picking it.

Nothing you already had breaks. Agents that name the old `heavy` / `standard` /
`light` tiers keep resolving to the same rung, and deployments configured through
the old `ModelTier:*` settings keep their mapping until you label your models.
