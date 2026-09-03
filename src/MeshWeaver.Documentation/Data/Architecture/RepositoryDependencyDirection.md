---
nodeType: Markdown
name: Repository Dependency Direction
category: Architecture
description: The dependency between the platform repo and the plugin repos runs ONE way. This is the measured inventory of every edge that runs the other way, what each one would do if the plugin repo were broken, which are defects, and which are the honest cost of the portal host living outside core.
icon: /static/NodeTypeIcons/code.svg
---

# Repository Dependency Direction

**`Systemorph/MeshWeaver` is the platform. `MeshWeaver.Plugins` and the four satellites consume it.
The dependency runs one way, and every edge that runs the other way is a way core's own build or
release can be broken by a repository it should not know about.**

Plugins depending on core is CORRECT and must not be "fixed": `$(MeshWeaverRoot)` project
references, the digest-pinned platform image the in-mesh gates compile against, and a plugin repo
*calling* core's reusable `node-repo-*.yml` / `plugin-build.yml` workflows are all the intended
shape. This page is only about the other direction.

Measured against `Systemorph/MeshWeaver@cea28557b`. Re-measure before acting on it; line numbers
move. See also [Carving Projects Out Of Core](../CarvingProjectsOutOfCore), which covers the moves
that CREATE these edges.

## The inventory

### A · The image lanes build plugin source — structural, not a defect to delete

`Memex.Portal.Distributed` (the portal host) and `Memex.Database.Migration` (the migration worker)
LIVE in MeshWeaver.Plugins since #2293. Core's image lanes therefore check that repo out and
`dotnet publish` from it. **Without the checkout there is no project to publish** — this is not
incidental coupling that can be tidied away.

| Where | What it does | If Plugins is broken or absent |
|---|---|---|
| `main-cd.yml:849` → `:974` | checks out Plugins @ `vars.MW_PLUGINS_REF \|\| main`, publishes `plugins-repo/src/Memex.Portal.Distributed` | a Plugins compile error turns core CD red; **no images publish for that core commit** |
| `main-cd.yml:1020` → `:1080` | same, for `Memex.Database.Migration` | ditto — and a missing migration tag is the 6.5 h production outage of 2026-08-27 (#2555) |
| `main-cd.yml:524` | `git ls-remote` the Plugins HEAD, to key the image set on the PAIR (#2622) | fails RED, deliberately: an unresolvable HEAD means the image identity cannot be established |
| `main-cd.yml:2094` (`plugins-modules`), `:2166` (`plugins-bake`) | core's CD packs and bakes the Plugins module bundles the portals adopt for this identity | a red leg means the sealed publication is missing for an identity the images already carry — **and it stops the whole fleet**: `notify-dependents` is gated on `needs.plugins-bake.result == 'success'` (`:1982`), so Education, Reinsurance, SocialMedia and Manufacturing are never told the platform released |
| `release-images.yml:34` → `:101`, `:114`, `:126` | the `v*.*.*` tag lane, same two projects | a tag release publishes no portal image |
| `edge-images.yml:39` → `:92`, `:105` | the manual edge channel, same two projects | edge builds fail |

🚨 **The blast radius is the FLEET, not this repo.** A broken plugins tree does not merely red a
core job: `notify-dependents` — the one fan-out that tells every satellite a platform release
happened — sits behind `needs.plugins-bake.result == 'success'`. So one repository's compile error
silences the release wave for four others, which then adopt nothing and report `FrameworkDeclined`
at their next fetch. That is the cost of the edge, stated plainly.

🚨 **`ref: main` is the dishonest part, not the checkout.** A live checkout of a sibling's moving
default branch means "which plugins commit shipped with which core commit" is decided by whoever
merged last, and the same core commit can build differently on two runs. The variable
`MW_PLUGINS_REF` makes that coupling *steerable* during an incident; it does not make it a pin.

**The honest seams, in order of increasing cost:**

1. **Pin, do not follow.** Core records the plugins commit it builds against — a lockfile in core,
   or `MW_PLUGINS_REF` set to a sha rather than `main` — and a plugins release proposes a bump the
   same way a package upgrade does. The image set already keys on the PAIR (`check-image-set.sh`
   takes the plugins short-sha), so the identity machinery is in place; what is missing is that the
   pair is *chosen* rather than *observed*.
2. **Move the image build to the repo that owns the host.** Plugins already builds and tests these
   projects; the lane that publishes them could live there and consume core as a released artifact.
   This inverts nothing else and removes every edge in the table.
3. **Move the hosts back to core.** Correct by construction and the largest change; it also gives
   back the NuGet publication a source move costs (see the sibling page).

Until one of those lands, the coupling stands and should be *stated*, not discovered.

### B · A gate whose subject is core, living in the plugin repo — a defect, fixed

`DocumentationLinkIntegrityTest` and `DocumentationEmbedIntegrityTest` pinned **this repository's**
doc tree (`src/MeshWeaver.Documentation/Data`) from `MeshWeaver.Plugins/src/MeshWeaver.AI.Test/`,
because they check the union of the `Doc`, `Agent` and `Skill` partitions and the latter two are
embedded in `MeshWeaver.AI`, which ships from there.

The consequence: **a core pull request that broke a doc link or a `@@` embed went green here and
turned a DIFFERENT, private repository red — hours later, on a change it did not make.** A required
gate sitting behind a sibling checkout is the sharpest form of an inverted dependency, because the
repo that owns the subject has no way to run it.

The fix is a SPLIT, never a delete: core now carries the `Doc`-only half in
`test/MeshWeaver.Documentation.Test/`, and the plugin repo keeps the union — the repo that holds
BOTH assemblies validates cross-partition links, which is the correct direction. Nothing became
unpinned; core simply stopped depending on someone else to notice.

🚨 **A gate that moves and leaves its subject unpinned is not fixed, it is unpinned — with nothing
turning red to say so.** When a gate cannot move whole, split it along the assembly boundary and
say in both copies what the other one covers.

### C · A core PR gate that reads the fleet — deliberate, and worth naming

`dotnet-test.yml:1768` (`shared-rules`) reads `AGENTS.md` from all seven repos and is a `needs:` of
`collect-results` (`:1955`), this repo's only required status check. **A shared-rule block that
drifts in a satellite turns core red and blocks core merges until it is fixed there.**

That is chosen rather than accidental, and the reasoning holds: a per-repo self-check cannot detect
the missed-repo case, because a repo with no pull request never runs its own gate — "six of seven
were updated" and "all seven were updated" produce identical evidence. The scheduled half
(`shared-rules.yml`) exists for the weeks nobody opens a pull request here.

It is listed anyway, because anyone auditing "does core's build touch plugins?" must be able to find
it rather than rediscover it.

`dotnet-test.yml` (`cross-repo-pair`) is the SECOND edge of this class, added for #2689. It resolves
the pull request a surface-removing change declares (`Pairs-with: Systemorph/MeshWeaver.Plugins#904`)
and refuses to let core merge until that counterpart is merged into its repo's default branch — see
[The Cross-Repo Pair Gate](../CrossRepoPairGate). It exists because a public type leaving core's
`src/` reddened MeshWeaver.Plugins' trunk for two hours on a change none of that repo's pull requests
had made, and core's CI cannot see it: **core does not build the plugin repos, so the coupling can
surface nowhere except CD, after the fact.**

🚨 **The distinction that makes this class permitted while a checkout is not**: *a checkout puts
another repository's SOURCE into core's build; an API read puts only a FACT about it into a verdict.*
Core still compiles, tests and ships with no sibling on disk. Every edge of this class is
ASSERTED as well as documented — `PlatformNeverDependsOnPluginsGuard.ApiReadLedger` enumerates them
and fails in both directions, so a new one has to be a decision rather than a diff nobody noticed.

`dependent-suites.yml` is the THIRD edge, added for #3103, and it is the one the first two were
waiting for. A pull request that changes the public declaration set asks MeshWeaver.Plugins — by
`repository_dispatch`, under an App token scoped to that repository — to build its `src/` and run
its suites against the pull request's **merge commit**, then reads the verdict the dependent
recorded in its own repository and finishes with it. Two things keep it on the permitted side of
the line: the dependent's source is compiled **there**, against a *candidate* core commit (an
observation about the commit, never a link into core's build — `dotnet build` here still needs no
sibling on disk), and the verdict travels by a **read** (the dependent writes a marker ref in its
own repository; core polls it with `contents: read`), never by the dependent writing into core.
See [The Cross-Repo Pair Gate](../CrossRepoPairGate) → *"The dependent's suites run from the core
pull request"*.

## What is NOT an inverted edge

Checked and cleared — do not "fix" these:

- **`plugin-build.yml`, `node-repo-{validate,compile-check,gate,tag-modules,publish-bake,module-pack}.yml`**
  — `workflow_call` only. A plugin repo calling core's reusable workflow is the intended shape.
  `plugin-build.yml` carries an explicit note that its cross-repo checkout was REMOVED and must not
  be re-added on the strength of a grep.
- **`notify-dependents.yml`** — core pushes a `repository_dispatch` wave and does not wait for it.
  The subscriber set is read at run time from the App's installation list, so no repo name is
  hard-coded and adding one is an App installation, not an edit here.
- **`scripts/check-type-forwards.py --sibling`** — the flag can resolve a departed type against a
  sibling checkout, and CI deliberately does NOT pass it (`dotnet-test.yml`): without the flag the
  gate is the conservative one, and passing it would make a core verdict depend on another repo's
  moving HEAD.
- **The ~100 prose mentions** in comments, and the repository NAME used as test data
  (`ReleaseFactTest`, `FrameworkReleaseBroadcasterTest`, the self-update suites). A string is not a
  dependency; none of these reads the repo.
- **Every `$(MeshWeaverRoot)` reference from Plugins into core** — twelve projects reference
  `MeshWeaver.Documentation.csproj` alone, three of them production. That is the arrow pointing the
  right way.

## The rule

**A gate belongs in the repository that owns what it measures.** When you move a project out of
core, the gates that measure the code STAY with the code — but a gate that measures core content
while merely *needing* a plugin assembly to do it belongs here, split along the assembly boundary.
And when core genuinely must build from a plugin repo, it pins a commit; it does not follow a
branch.
