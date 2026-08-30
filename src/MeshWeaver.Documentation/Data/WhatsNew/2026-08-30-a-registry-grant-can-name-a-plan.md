---
Name: A registry grant can name a plan
Category: Feature
Description: What a registered installation may pull from the plugin registry can now be scoped to a subscription plan — personal, pro, dedicated, enterprise — so one grant entry, or one registration key minted for a plan, licenses exactly the packages that plan covers.
Icon: Sparkle
Order: -20260830
---

# A registry grant can name a plan

Until now a plugin registry could tell a registered installation only two things about a source:
every package, or one package by name. The Store, meanwhile, sells **plans** — Personal, Pro, a
dedicated instance of your own, Enterprise — and every package already declares which plan it
belongs to. The two never met: licensing a customer's instance "as far as the Pro plan reaches"
meant an admin typing package names, and keeping that list current by hand.

A grant entry can now carry the plan — `Plugins/*@pro` — and licenses exactly the packages of that
source the plan covers: everything ranked at or below it, everything the platform ships as baseline,
and, on the all-access dedicated plan, everything there is. The ranks come from the registry's own
plan nodes, the same ones the Store's pricing page renders, so re-ranking a plan or adding one is a
node edit and the registry follows.

A registration key can be minted for a plan too. Every installation that registers with it is
enrolled into that plan on first boot — one key for Pro customers, one for dedicated instances, none
of them needing a grant typed per install. Keys and entries without a plan behave exactly as before.

And a registry can open its door to the free tier: configure one such key as the registry's open
registration key and an installation that presents no key at all — a Homebrew install that names
only its instance id — lands on the free plan by itself. Moving it up is an admin's decision on the
registry, and a registry that configures no open key refuses un-keyed registrations exactly as it
always has.
