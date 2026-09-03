---
Name: A module built once is not built again
Category: Feature
Description: The module build lane now records every build on the registry portal, keyed by everything that went into it — so a second run of the same bytes reuses the first, two runs never build the same thing at once, and a platform release rebuilds only what its new identity actually changes.
Icon: ArrowSync
Order: -20260902
---

# A module built once is not built again

Every package that ships a module used to be compiled, packed and tested from scratch on every run
that reached it: once on the pull request, again on the push to `main` minutes later, and once more
in every concurrent pull request that happened to touch the same module. A platform release rebuilt
every module in the fleet, whether or not the release changed anything the module could see.

The lane now writes a **module build ledger** on the registry portal. Every selected module gets a
content address — the package's content hash, the source of every in-repo project the bundle carries
or its tests reach, both image digests, the platform commit and the lane's own recipe — and the ledger
says, per address, what has already happened: built, tested, published, failed, or in progress right
now in another run.

- **Same address, built before?** The run downloads that bundle and runs only what is still missing
  — the tests if nobody recorded a verdict, the hand-over to the registry if nobody published it.
- **Same address, another run is on it?** The run waits for that one to finish rather than building
  the same thing beside it.
- **Same address, compile failed?** The run stops with the other run's evidence — the same inputs
  give the same result. A test failure is allowed one more attempt, so a flaky suite does not pin
  the fleet.

The registry portal being unavailable never turns a green build red: the lane then builds without
coordination and says so in the job summary.

Where the design decisions live: [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture)
→ "Content-addressed outputs".
