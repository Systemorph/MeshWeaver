---
Name: Every model sits under its own provider
Category: Fix
Description: Models whose id carries a vendor prefix — OpenRouter's z-ai/glm-5.2, moonshotai/kimi-k3 — no longer lose their heading and pile up under the provider above them, and a provider now shows the readable name it always declared instead of its wire name.
Icon: AppsList
Order: -20260819
---

# Every model sits under its own provider

Open **Choose a model** and the list is grouped by provider, each group under its
own heading. Two things had gone wrong with those headings, and together they made
the dialog read as though models belonged to providers they had nothing to do with.

**A model whose id carries a vendor prefix lost its heading.** OpenRouter names its
models `vendor/model` — `z-ai/glm-5.2`, `moonshotai/kimi-k3`. The picker worked out
a model's group by trimming the last piece off its address, which is right up until
the *name itself* contains a slash: it then landed one level too deep and matched no
provider at all. The consequence was not a missing row but a misleading one. The
OpenRouter heading disappeared entirely — nothing appeared to belong under it — and
its models, still in the list, came to rest directly beneath whichever group sorted
above them. On the memex portal that was Azure Foundry, so two OpenRouter models sat
under an Azure Foundry heading, looking for all the world like Azure Foundry models.

Now the group is taken from the model's own address rather than reconstructed from
its name, so a slash in the name changes nothing. GLM 5.2 and Kimi K3 appear under
**OpenRouter**, and the OpenRouter heading is back.

**A provider showed its wire name.** Each provider ships a readable label — the
Azure Foundry one has said *Azure Foundry* all along — but the heading was drawn
from the internal identifier instead, so it read `AZUREFOUNDRY`, jammed together.
It now shows the declared label. Your saved model choice is untouched: only the
heading changed, not the provider's identity or any model's address.

One thing this does not do: if your portal has ended up with **two** provider entries
for the same upstream service, both still appear. That is a duplicate in the data
rather than a display fault, and an administrator has to merge them.
