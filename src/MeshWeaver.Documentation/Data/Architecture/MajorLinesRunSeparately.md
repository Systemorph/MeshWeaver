---
Name: Major Lines Run Separately
Category: Architecture
Description: A new platform major launches as its own line beside the old one — every repository's main is v4 and main3 is v3, each with its own build-server channel (latest4, latest3), images, SDK and resolution — so a major bump never changes what a dependent on the previous major builds, tests or runs.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3v18"/><path d="M18 3v18"/><path d="M6 8h12"/></svg>
---

# Major Lines Run Separately

> **Status: DECIDED DESIGN — not wired yet.** Policy `major-lines-run-separately`. The steps in §5
> land in order. Until the last of them, the platform has ONE line, and no major bump may merge.

A platform major is a LINE, not a version number. **v4 launches as v4 and is different from v3**:
its own branch, its own build-server channel, its own images and SDK, its own resolution. A
dependent that has not opted into v4 keeps building, testing and deploying on v3 exactly as
before. A v4 merge cannot reach it.

## 1. Why — the incident that set it

Core #8129 (.NET 11, `PlatformVersion` 4.0.0) merged into the single shared line on 2026-11-12
21:28Z. The control plane's PR steward merged it the moment it was green and reviewed. One line
carried two majors, so:

| What broke | Mechanism |
|---|---|
| Core CD failed every run for about 9 h, and nothing was delivered | The image jobs check out MeshWeaver.Plugins **main** (still net10.0) and restore it against core's net11-only projects: `NU1201: Project MeshWeaver.Connection.Orleans is not compatible with net10.0`. |
| Every MeshWeaver.Plugins pull request went red | Satellites and Plugins call core's reusable workflows `@main`, and core main now installed SDK 11: `dotnet --version answered '11.0.100', not an exact 10.0 SDK version`. |
| SocialMedia and Manufacturing main went red | They check out core **main** and build it as net10. |
| The v4 dependents could not go green either | Plugins #5421 waits for a sealed 4.0 set, which CD could not build: a deadlock. |

Nothing LIVE moved: no 4.0 set was promoted, so the build server and every instance stayed on 3.1.
It was reverted by #8132. One line for two majors makes every major bump a fleet-wide CI outage.

## 2. The lines

| | v3 | v4 |
|---|---|---|
| branch, in every repository that has a v3 tree | `main3`, cut from main as it stands before v4 re-lands (core: `53542a44c9`, the tree every instance runs now) | `main` |
| set names | `3.1.<run>` | `4.0.<run>` |
| image pointers | `3-latest`, `3.1-latest` (already per major: `.github/release-channels.json` is templated on `{major}`) | `4-latest`, `4.0-latest` |
| build-server channel | `latest3` | `latest4` |
| SDK / TFM | 10.0 / `net10.0` | 11.0 / `net11.0` |
| instances | `updatePattern: "3.*"` | `updatePattern: "4.*"` |

Each line's CD advances only its own pointers and its own channel. A hand-over that would move a
channel to a set of another major is REFUSED, the same way a backwards move is refused today
(MeshWeaver.Plugins `BuildHostUpdates.Decide`). `latest` stays as an alias of the newest line's
channel only while a dependent still reads it, and every reader moves to the explicit channel.

## 3. How a dependent picks its line

- **The floor names the major.** A dependent's declared platform floor (MeshWeaver.Plugins
  `scripts/platform-floor.json`, a package's `minMeshVersion`) selects the line: major 3 reads
  `latest3`, major 4 reads `latest4`. Raising the floor across a major is the one act that moves
  a dependent to the next line, and it is made in that dependent's own pull request (policy
  `request-min-version-wait-until-deployed`). Resolution never crosses a major upward on its
  own.
- **Shared workflows by branch.** `uses:` cannot take an expression, so a v3 dependent calls
  `Systemorph/MeshWeaver/.github/workflows/<lane>.yml@main3`, and a v4 dependent calls
  `@main`. Measured 2026-11-13: 92 such references in MeshWeaver.Plugins, 16–30 in each
  satellite, 4 in Deploy and 4 in Memex. They all point at `@main` today.
- **The resolver lists the dependent's line.** `resolve-platform.py` lists `main-cd.yml` runs only
  on the line it serves. That line is `--line`, `MW_CORE_LINE`, or the `scripts-ref` it was
  fetched at, and the shared lanes pass that ref. With none of them it falls back to the default
  line and warns. So a `main3` caller resolves `3.1.N` and never a `4.0.N` set; only a freeze may
  name another line's set, and the run warns when it does. See
  [CD Delivery Lines](../CdDeliveryLines), "The resolver serves ONE line".
- **The SDK follows the resolved platform.** A shared lane installs the SDK the platform it
  resolved declares, never a version core's branch hard-codes, so one lane file can serve either
  line without guessing.

## 4. Every repository with a v3 tree gets `main3`

The owner decided on a dedicated `main3` branch for the v3 line, with `main` the line for v4. That
applies to MeshWeaver.Plugins too. Core CD builds the portal and control images against a Plugins
commit (`gate.outputs.plugins_sha`), so the v3 line's CD takes that commit from Plugins
**`main3`** and the v4 line's from Plugins `main`. No image ever mixes majors, and v3 stays a
real line that a fix can still land on, not a frozen commit. Every PLUGIN repository gets a `main3` as well: MeshWeaver.Plugins and each satellite (Education, Reinsurance, SocialMedia, Manufacturing, Crm, FundReporting). Each `main3` calls core's lanes `@main3`, and each `main` calls them `@main` once it has opted into v4. In-mesh modules are baked per live framework identity (policy
`bake-per-live-identity`), so v3 instances keep receiving bundles built for `c003…`.

## 4a. Versions follow the line

On the v4 line every package and module version is `4.0.0`, and every floor (`content.minMeshVersion`,
MeshWeaver.Plugins `scripts/platform-floor.json`) names the first sealed 4.0 set. The `main3` copies keep
their v3 versions (1.x / 0.x), so a version number says which line it belongs to. The renumbering is made
once, in each repository's v4 opt-in pull request.

## 4b. The plugin registry serves each line

The registry lists what MeshWeaver.Plugins `main` holds. Once `main` is v4, a v3 instance must still be
offered v3 packages: the registry serves `main3` to instances whose record says `updatePattern: "3.*"`,
and `main` to `4.*`.

**The package listing is served per line.** Every consumer states its line on each listing request
(`PlatformLine`, the `line` query parameter), and the registry answers from the ref its record declares
for that line (`lineRefs`).

**The module bundle index is read per line, by the consumer.** Before the registry kept one shelf per
line (below), the bundle index (`/api/plugins/bundles/index.json`) was not chosen by line: it listed every
module generation the registry's shelf held, newest version first, whatever line built it. A v3 registry shelved the v4 AI
build (4.0.1) as AI's head next to the v3 one, so every v3 consumer that took the first entry was
offered 4.0.1 and refused it at landing, and never got a newer v3 AI (MeshWeaver.Plugins#5550). The
floor hold did not stop it: two published builds are ordered by run number, the run counter is shared
across lines, and so a `4.0.13296` floor reads as met on `3.1.13396`.

So the client splits the index by line before anything picks from it (`BundleLine`, in
`PluginBundleClient`'s index read). Every pick of a package's bundle works from that split index: module
landing, NodeType adoption, shipped builds and the held-NodeType retry. Module version numbers say
nothing about the line (v3 AI is 1.52.0, v4 AI is 4.0.1), so the line of a module entry is read from what
the entry states about its platform. The first of these that names a major decides:

1. The entry's own `frameworkMvid`, when it is a compatibility key (`c004e001` → 4). This is a fact
   about the bytes, recorded by their producer.
2. The major of its declared floor (`minMeshVersion`, `4.0.13296` → 4).
3. The registry's own line, which is the index-level compatibility key. This covers an entry that states
   neither, such as the registry's own install record or a generation shelved before producers stated
   anything.
4. Otherwise, the legacy shared line (3). Every producer that states nothing predates the split.

Every major up to 3 counts as one line. An entry with no module is a NodeType bundle the registry
assembles itself. Its line is the index-level identity, which the NodeType gate already compares, so it
is not filtered again.

A package offered only as builds of another line lands nothing. Its landing verdict says so, naming each
withheld build, its line and the evidence (`SkipNoBundle — the registry advertises AI only as build(s)
for another platform line — AI 4.0.1 (line 4, by its framework identity c004e001) …`). Each index read
logs the withheld builds once.

**The registry keeps one shelf per line, and serves each caller its own.** Core carries the shelf and the client half; the registry routes in MeshWeaver.Plugins wire them (MeshWeaver.Plugins#5550). Reading the index per line protects the consumer, but not the shelf itself.
The shelf kept one head per module, the highest version, plus one retained previous generation. The v4
AI build 4.0.1 became the head, the v3 build survived only as the fallback, and the next v4 publish
(4.0.2) would have pushed the last v3 build off entirely. So:

- **Each line has its own shelf.** The registry's own line is its own landing service, unchanged. Every
  other line gets a landing service of its own (`ModuleShelfLines`), rooted at the tree a process of that
  line lands into on the same volume: `<root>/lines/<major>/` beside a v3 root, or the root itself for
  line 3 on a v4 registry (`ModuleRoot.ForLine`). Without a configured root, the other line's tree is
  `<own tree>/lines/<major>/`.
- **An upload is shelved by the line it states.** The publish route reads the upload's framework identity,
  then its declared floor, with the same rules `BundleLine` applies to an index entry
  (`BundleLine.StatedLine`). An upload that states neither is the registry's own line, as every upload
  was before. The highest version of a line is that line's head; a v4 publish never moves the v3 head, and
  the other way round.
- **The index and the bundle route answer per caller line.** Both take a `line` query parameter, the same
  wire name as the package listing (`PlatformLine.WireName`), and every client now sends it. A caller that
  predates the parameter is read by the line of the framework identity it already sends (`identity=c003e005`
  → 3, `BundleLine.CallerLine`). A caller stating neither gets the registry's own line, as before. The
  answer lists only that line's heads and fallbacks. On a tree that still holds builds of another line,
  shelved before the split, those entries are left out of the answer, so the line's retained fallback is
  served instead.
- **The client keeps splitting the index.** That stays as defence in depth, for a registry that predates
  the parameter and ignores it.

**A floor on another line is never met.** `PlatformFloor` compares the line before the run number. A
floor on a higher line holds whatever the run numbers say (`4.0.13296` on `3.1.13396` is held). A floor
on a lower line is advisory, never satisfied, and the bundle rule above keeps that line's bytes away.

What this does not do yet: the modules GC sweeps only the process's own tree, so generations on another
line's shelf are collected by a process of that line on the same volume, and accumulate on a registry
that no process of that line shares.

**Dependency floors are judged per line too.** Install records (`Plugins/*`) are shared by the whole
mesh, but the landed module tree is per line (a v4 image lands under `lines/4/`). The module-set proposal
(`ModuleDependencyFloor.Unmet`) therefore judges only records of its own line: a record whose
`minMeshVersion` names another line describes a build this tree does not load. Before this rule, a v3
Chat record requiring `AI@^1.51.0`, judged against the v4 tree's AI 4.0.1, refused every v4 proposal. A
record that states no floor is still judged.

## 4c. Each line has its own first instance

Policy `line-first-instance` (MeshWeaver#8365). The fleet of a line is offered a set only after ONE
instance runs it — "control first" is the case of the line control follows, not a rule about
control. Once control moved to v4, the v3 line had no instance that could run its builds: the arm
read `control runs <a v4 commit>, which does not contain <the v3 set> yet` for nine hours, while four
promoted 3.x sets waited behind it.

**The declaration.** `.github/delivery-lines.json` names the line's first instance:

```json
{ "branch": "main3", …, "firstInstance": "memex", "firstInstanceImageRepository": "memex-portal-ai-first3" }
```

Both fields absent means the control instance on `memex-control`, handed its builds by
`control-first`. That case is unchanged. `cd-line.py` refuses the following, each by name:
- a blank or malformed id;
- the control instance spelled out;
- an id without its repository, or a repository without an id;
- a repository that is not a plain name;
- the shared repositories `memex-portal-ai`, `memex-control` and `memex-migration`;
- two lines on one repository.

**Why the instance has its own image repository.** An instance on the fleet's `memex-portal-ai` can
roll only to a `memex-portal-ai:<version>` tag. That tag is the ARMING write: `arm` alone writes it,
and every Continuous instance on that repository sees it at once. So an instance on the fleet's
repository can never run a set before the fleet is offered it, and gating the arm on such an
instance is a wait that cannot end. The first instance therefore runs a repository of its own,
exactly as control runs `memex-control`.

**The handover.** For a line that declares a first instance, main-cd's `first-instance-first` job
does the following after `promote`, for every promoted set:
1. It tags `<firstInstanceImageRepository>:<version>` from the portal's staging image, on cr first
   and ACR last. It writes the version tag only: nothing seeds a new instance from this repository.
2. It sends the instance's record the signed `self-update-available`.

These are the same two decisions `control-first` takes (`control_first`, `control_announcement`,
and the one delivery script `post-self-update-announcement.sh`), aimed at another record. It
announces only a build of the major the instance runs (`control-line --instance-url`). It also
announces only when the instance's record names that repository: until the record moves, the build
is tagged and not announced, and the job says so in a warning.

**The arm.** `select` reads the line's first instance as it reads control:
- **What it reads.** The record sits beside control's (`mesh/Deployments/<id>.json`, read with the
  fleet-reader App). `/api/version` and `/health` are read at `https://` + the record's
  `content.host`. The lag clock starts at that line's `Deploy the line's first instance first` job.
  The bound is `platformLagBoundMinutes`.
- **What it says.** Every sentence names the instance and the line (`memex runs …`; `FLEET DELIVERY
  FROZEN ON memex (the first instance of line main3)`).
- **What holds a line.** Each of these is a declaration. It arms nothing and is a warning, never the
  frozen red:
  - the instance's record excludes the line's major (`updatePattern`);
  - the instance's record still names another repository;
  - the line declares no first instance while control's record excludes its major. This reading is
    `LINE HAS NO FIRST INSTANCE`, and it names the missing declaration.

**Moving the instance onto its repository.** The record's `imageRepository` moves to
`<registry>/<firstInstanceImageRepository>` only after that repository carries a tag. The record also
sets `migrationImageRepository` explicitly, because the derived name (`imageRepository` with
`memex-portal-ai` replaced by `memex-migration`) names a repository nobody writes. Until the record
moves, the line is held, and the hold says so.

## 5. Order of work

Each step is its own pull request and lands before the next begins:

1. **Revert** the premature v4 merge — #8132.
2. **Cut `main3`** in core (= `53542a44c9`) and in MeshWeaver.Plugins (its main as it stands).
   Each gets the ruleset main has (the same required checks). Core's CD triggers on both branches
   (`main-cd.yml` `branches:`), and each line's CD reads the Plugins commit of its own line.
   🚨 A ruleset is a maintainer's change.
3. **Dependents name their line**: every `uses: …@main` → `@main3` in Plugins, the
   satellites, Deploy and Memex, and then each plugin repository cuts its own `main3` from that `main`, and every core checkout pinned to the line. The two branches are
   identical at this point, so this changes no behaviour and can land in any order.
4. **Per-major build-server channels**: `latest3` and `latest4` on the operations record, the
   cross-major refusal in `BuildHostUpdates`, and the resolver reading the channel of the floor's
   major.
5. **Core main re-lands v4** (the revert of #8132). Only the v4 line is affected; v3 dependents
   stay on `main3` and `latest3`.
6. **Dependents opt in one by one**: Plugins #5421, then each satellite. Each raises its floor to
   4.x and points its `uses:` at `@main` in the same pull request. Control moves to `4.*` first,
   then the other instances.

A declared-break pull request is opened as a DRAFT and stays a draft until its step is reached.
The steward merges any green, reviewed, non-draft pull request, which is how #8129 merged early.
