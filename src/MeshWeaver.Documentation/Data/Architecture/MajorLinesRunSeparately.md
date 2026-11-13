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
- **The SDK follows the resolved platform.** A shared lane installs the SDK the platform it
  resolved declares, never a version core's branch hard-codes, so one lane file can serve either
  line without guessing.

## 4. Every repository with a v3 tree gets `main3`

The owner decided on a dedicated `main3` branch for the v3 line, with `main` the line for v4. That
applies to MeshWeaver.Plugins too. Core CD builds the portal and control images against a Plugins
commit (`gate.outputs.plugins_sha`), so the v3 line's CD takes that commit from Plugins
**`main3`** and the v4 line's from Plugins `main`. No image ever mixes majors, and v3 stays a
real line that a fix can still land on, not a frozen commit. A satellite gets a `main3` only if it
must keep shipping v3 changes. One that simply waits for v4 stays on `main` and calls core's lanes
`@main3` until it opts in. In-mesh modules are baked per live framework identity (policy
`bake-per-live-identity`), so v3 instances keep receiving bundles built for `c003…`.

## 5. Order of work

Each step is its own pull request and lands before the next begins:

1. **Revert** the premature v4 merge — #8132.
2. **Cut `main3`** in core (= `53542a44c9`) and in MeshWeaver.Plugins (its main as it stands).
   Each gets the ruleset main has (the same required checks). Core's CD triggers on both branches
   (`main-cd.yml` `branches:`), and each line's CD reads the Plugins commit of its own line.
   🚨 A ruleset is a maintainer's change.
3. **Dependents name their line**: every `uses: …@main` → `@main3` in Plugins, the
   satellites, Deploy and Memex, and every core checkout pinned to the line. The two branches are
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
