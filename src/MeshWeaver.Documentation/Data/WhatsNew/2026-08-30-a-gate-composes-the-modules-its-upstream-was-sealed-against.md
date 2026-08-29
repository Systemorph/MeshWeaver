---
Name: A gate composes the modules its upstream was sealed against
Category: Fix
Description: Every node repository's gate went red at once on "dependency record mismatch" because it composed AI and Collaboration from the registry while its upstream's assemblies were sealed against a different build of them. A publication now carries the module bundles it composed, the registry serves them, and gates take them from there — never the package endpoint.
Icon: LinkMultiple
Order: -20260830
---

# A gate composes the modules its upstream was sealed against

A node repository's gate installs the packages it depends on from a *sealed publication* — the
assemblies its upstream baked for exactly the platform image the gate runs. Those assemblies record
which build of each module they were compiled against. The gate, however, fetched the modules
themselves — `AI`, `Essentials` — from the registry's package endpoint, which serves whatever the
module's own lane published last, under a version number that does not change when a rebuild
changes the bytes.

The moment those two disagreed, every gate in the fleet declined every prebuilt assembly it
installed (`dependency record mismatch — built against mvid:…, live is mvid:…`), compiled the
sources itself instead, and then failed its own postcondition for having judged bytes that will not
ship. Reinsurance, SocialMedia, Crm, Manufacturing and Education were red at the same time, and
none of them had changed anything.

A publication now travels with the module bundles its bake composed, listed in their own index that
is written before the seal. The registry serves that set beside the publication's bundles, and a
gate that declares its upstream takes each module from the first upstream whose seal lists it. There
is deliberately no fallback: an upstream with no seal for the identity, a seal from before this
change, or a module no upstream sealed fails red naming what to republish — the registry's bytes
would reproduce the same mismatch under a green tick. A publication sealed before this change is
republished with its module set on the source's next bake, so the fleet converges without anyone
re-baking by hand.
