---
Name: Your installation keeps running what it has, and takes what loads
Category: Feature
Description: A new rule for add-ons — if no newer version ships for the platform you run, the one you have keeps running; a version string an add-on declares no longer decides whether it loads, a measurement does; and the moment a newer version that loads is published, you get it. Set on 2026-09-07 after every production portal spent a day held on a morning build by a version string.
Icon: Box
Order: -20260907
---

# Your installation keeps running what it has, and takes what loads

Three things are now true of every add-on (module) on every installation, by policy:

1. **If no newer version ships for the platform you run, the version you have keeps running.** A newer build that cannot load on your platform never removes the one that can.
2. **Whether an add-on loads is measured, not declared.** An add-on used to carry a minimum platform version as text, and a mismatch in that text blocked it — even when the add-on would have loaded perfectly. Today, 2026-09-07, that text held every production portal on the morning's build for the whole day. From now on the platform checks the add-on's bytes against what is actually running; the declared version is shown on the add-on's row for information and decides nothing.
3. **The moment a newer version that loads is published, you get it** — within minutes, not at your next restart of convenience.

The rule and the plan that implements it are on [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy). Until the first step lands, the existing behaviour applies and the pages describing it carry a banner saying so.
