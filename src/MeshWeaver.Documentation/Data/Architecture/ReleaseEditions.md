---
Name: Release Editions and the Declared Labels
Category: Architecture
Description: Every moving image label (3-latest, 3.1-stable, 3-mac, 3-enterprise, main) is declared in one file that retention reads as protected. This page covers who writes each label and the gaps to close before a Mac Studio or Mac mini can host an instance.
---

# Release Editions and the Declared Labels

An **edition** is a technology or support target the platform ships to: the standard Linux
edition (AKS and any Kubernetes host), the **Mac** edition (Apple-silicon hosts such as a Mac
Studio or a Mac mini running Colima k3s) and the **enterprise** edition (installations that
follow official releases only). A **channel label** is the moving image tag that names the build
an edition currently offers, for example `3-latest`, `3-mac` or `3-enterprise`.

[Release Channels](../ReleaseChannels) says how proven a build is (`latest`, `stable`). An edition
says what the build targets. Both are spelled as moving image labels, and both are declared in the
same file.

A channel label is a tag, not a version. Versions stay plain SemVer and never carry a channel word
(policy `platform-semver-versioning`). A deployment seeds from a label and then runs a resolved
**version**: a pointer is a seed, never a running pin
([ImageLine](https://github.com/Systemorph/MeshWeaver.Plugins/blob/main/Hosting/Deployment/Source/ImageLine.cs)).

## Declared in one place

Every moving label lives in
[`.github/release-channels.json`](https://github.com/Systemorph/MeshWeaver/blob/main/.github/release-channels.json)
(policy `declared-release-channels`). Its only reader is `.github/scripts/release-channels.py`:

| Command | Who calls it |
|---|---|
| `release-channels.py protected-patterns` | a pruner: the JSON list of label patterns it must exclude by name |
| `release-channels.py is-protected <tag>` | a shell step: exit 0 means protected, 1 means not a declared label, and 2 means the lookup failed. A caller must abort on 2 and never read it as unprotected |
| `release-channels.py tags --version <v> --writer <workflow> [--job <job>] [--repository <r>]` | a writer: the `written` labels that binding moves for that set |
| `release-channels.py check .` | every pull request (`CI's own shell`) |
| `release-channels.py --self-test` | every pull request: shows that each check can fail |

### The labels

| Label | Edition | Meaning | Writer | Status |
|---|---|---|---|---|
| `{major}-latest` | standard | newest **armed** set of the major, multi-arch | `main-cd.yml` `arm` Phase D, `control-first` | written |
| `{major}.{minor}-latest` | standard | newest armed set of one minor line | same | written |
| `{major}.{minor}.{patch}-latest` | standard | legacy notation's patch line (pre-SemVer sets only) | same | written |
| `{major}.{minor}-stable` | standard | newest set of a minor line that earned trust ([Release Channels](../ReleaseChannels)) | the stable promoter (owed) | declared |
| `{major}-mac` | mac | newest armed set whose `linux/arm64` variant passed Mac acceptance on Apple silicon | `main-cd.yml` `mac-accept` (owed) | declared |
| `{major}-enterprise` | enterprise | newest **official** release of the major (clean `x.y.z`) | `release.yml` (owed) | declared |
| `main` | continuous | newest promoted set, before arming | `main-cd.yml` `promote` | written |
| `latest` | continuous | the tester's moving tag; GHCR's latest release | `main-cd.yml` `promote`, `release.yml` | written |

Each label lists its **writer bindings**. A binding names a workflow, a job, and the repositories
and registries that job writes the label to. For example, `promote` writes `latest` only to
`mw-plugin-test`, while `release.yml` writes it to GHCR for every released repository. A pruner
protects a label in the union of its bindings' repositories.

`declared` means the label is reserved and **already protected**, but no workflow writes it yet. A
label is therefore protected before its first write, which is the order
[Released Artifact Retention](../ReleasedArtifactRetention) requires: protect a set before
advertising it.

Mac Studio and Mac mini are **host profiles** of the one Mac edition, not separate labels. Both run
the same `linux/arm64` image inside Colima's VM. What differs is the hardware: the VM size and the
local language model the host's native Ollama can hold in unified memory. One image under two tags
would be one set of bytes protected and rolled twice. If the two ever need different bytes, add a
second entry, for example `{major}-mac-studio`.

## How retention reads it

- **Excluded by name, never locked.** A label moves, so locking the digest it names tonight
  would pin bytes the label leaves tomorrow. `lock-pinned-digests.py` classifies every declared
  label as floating, through the reader. A missing or malformed file raises an error instead of
  silently classifying fewer labels. A pruner skips each matching tag and the manifest that tag
  names at the moment of the sweep.
- **Every status is protected.** `written`, `declared` and `retired` labels are all excluded.
  Removing an entry is the only way to release a label, and that takes a reviewed pull request.
- **Never shaped like a version.** The self-updater (`VersionSelect`) selects every tag matching
  `^\d+\.\d+\.\d+([-+].*)?$` except those ending in `-latest`. A label such as `3.1.0-mac` would be
  rolled to as if it were a build. `check` refuses that shape, so a new label is `{major}-<word>`,
  `{major}.{minor}-<word>`, or ends in `-latest`. This is why only `{major}.{minor}-stable` is
  declared. The legacy-shaped `3.0.0-stable` that [Release Channels](../ReleaseChannels) uses as an
  example matches `PlatformVersionTag` and not `MovingPointer`. Before anything writes that shape,
  `VersionSelect` must exclude `-stable` as it excludes `-latest`.
- **Held to the writers.** `check` renders sample sets in both notations and requires
  `platform-version.py line-pointers` (the speller main-cd uses today) to equal the `written`
  `main-cd.yml#arm` labels. If CD moves a pointer the file does not declare, the check fails.

Not labels, and not in this file: per-version markers such as `arm-complete-<version>` and
`staging-<sha>-<run>`. They belong to the publication records that
[Released Artifact Retention](../ReleasedArtifactRetention) inventories.

## Activating a declared label

1. **`{major}.{minor}-stable`**: the stable promoter described in
   [Release Channels](../ReleaseChannels) writes it, then flips the entry.
2. **`{major}-enterprise`**: wire `release.yml` to move the label forward only, after the release
   marker, through `release-channels.py tags --writer release.yml`. Then flip the entry's `status`
   to `written` in the same pull request. Until then `tags` hands the writer nothing.
3. **`{major}-mac`**: needs the Mac acceptance lane below, then the same flip.

## The Mac deployment gap

**What exists:**

- Multi-arch platform images (`linux/amd64` and `linux/arm64`), which run natively in Colima's
  arm64 VM.
- The [`memex-local`](https://github.com/Systemorph/MeshWeaver/blob/main/deploy/homebrew/README.md)
  Homebrew CLI: Colima k3s, the same Helm chart as AKS, ingress-nginx with mkcert TLS, Postgres
  on a PVC, native Ollama, registry mode, an `autoroll` launchd watcher and a port-forward
  LaunchAgent. See [LocalColimaMac](../LocalColimaMac) and [MacLocalStack](../MacLocalStack).
- `homebrew.yml`, which installs and tests the formula on a hosted macOS runner. It tests the CLI
  under bash 3.2. It does not run a cluster.

**What a Mac host still lacks.** Everything above is built for a developer's laptop. An always-on
Mac Studio or Mac mini that serves an instance needs the following:

| # | Gap | What exists | What is needed |
|---|---|---|---|
| G1 | **Channel** | `memex-local` and `autoroll` pull `:main`, the promoted set before arming and before the ladder verdict | follow a declared label (`3-mac`, or `3-latest` until it exists), resolve it to the version the image reports, and deploy that version |
| G2 | **Pull credential** | `az acr login`, which needs a person's Entra session with AcrPull | a scoped, read-only pull token for the fleet registry, stored in the host's keychain and rotated |
| G3 | **Boot persistence** | Colima starts only on `memex-local up`. The LaunchAgents run only after that user logs in to the GUI | Colima started at boot, the watchers as boot-time jobs (or auto-login on a dedicated account), sleep disabled and restart after power loss (`pmset`) |
| G4 | **Hardware profiles** | one VM size (8 CPU / 16 GiB) and one model choice | `mac-studio` and `mac-mini` profiles: VM CPU and memory, and the Ollama model the host's unified memory can hold |
| G5 | **Fleet record and roll lane** | `DeploymentContent` assumes AKS (cluster, Key Vault), and the control lane rolls through `aks-ops.yml` | a record shape for a Mac host, rolled host-side by its own watcher. The control plane observes the host but cannot reach its cluster |
| G6 | **Retention visibility** | AXIS 3 asks every declared installation's `/api/version` | the host reachable through a tunnel, or declared in `.github/acr-retention/instances.json`. Otherwise the digest it runs is unprotected |
| G7 | **Acceptance** | nothing proves an arm64 set boots on Apple silicon. Hosted arm64 macOS runners cannot run Colima's VM (no nested virtualization) | a self-hosted Apple-silicon runner (a Mac mini) running `memex-local up` and `verify` against the candidate set. On green it writes `3-mac` (the `mac-accept` job) |
| G8 | **Ingress and identity** | `memex.localhost` with mkcert, reachable from the host only | a real hostname and certificate (ACME, or a tunnel that terminates TLS), and the Entra redirect URIs for it |
| G10 | **Shared artifact store** | each portal resolves bundles from the store its deployment mounts | the host consumes its control's store read-only (below). The `linux/arm64` bundles for a set exist before `3-mac` is written |
| G9 | **Backups and telemetry** | Postgres lives on the Colima VM disk. Logs stay on the host | database dumps to the estate's backup store, and logs and metrics shipped to the estate's observability stack |

### Mac hosts and the shared artifact store

The estate's compiled modules and bundles are produced **once** by the build server and the control
instance. They are kept in a control-owned shared artifact store. Portals mount that store
read-only, and a client environment's control syncs it one way from the upstream control. A Mac host
fits this model as an ordinary portal:

- **It consumes, never produces.** A Mac host mounts its control's store read-only when the two
  share a network. Otherwise it syncs one way from that control, the same way a client control
  syncs from upstream. It never writes into a shared store.
- **Its builds are its own identity.** The Mac edition runs `linux/arm64`. That framework identity
  differs from the `linux/amd64` identity AKS runs, so its compiled bundles are stored and
  addressed under their own identity key, beside the amd64 ones and never in place of them. The
  store's sweep counts the arm64 identity as a represented identity and keeps its latest sealed
  publication like any other.
- **`3-mac` waits for its bundles.** If only amd64 bundles exist for a set, a Mac host refuses
  adoption and compiles from live source. That works, but slowly. So `mac-accept` requires the
  set's `linux/arm64` bundles to be present (main-cd's arm64 bake leg) before it moves `3-mac`.
- **The prune job reads the same declaration.** It skips every label in
  `.github/release-channels.json` across all of a label's repositories, so the image a Mac host
  seeds from survives while the label names it.

**Order of delivery:**

1. **Declare the labels** and make retention read them. This page and its pull request do that.
2. **G1 and G4 in `memex-local`.** Add `--channel <label>` to follow a declared label and pin the
   resolved version, and add `--profile mac-studio|mac-mini`. These are repository changes with
   bash 3.2 tests.
3. **G2 and G3.** A host-install step: the keychain-held pull token, Colima at boot, and the power
   settings. These need a maintainer-issued registry token.
4. **G7.** Register a self-hosted Apple-silicon runner, add `mac-accept` to `main-cd.yml`, and flip
   `{major}-mac` to `written`.
5. **G5, G6, G8 and G9.** Write the Mac host's deployment record, tunnel, backups and telemetry
   when the first host is commissioned.

Steps 3 to 5 change live systems: a registry credential, a runner registration, a live host. A
maintainer runs them. The repository changes prepare each one.

See also: [Release Channels](../ReleaseChannels) · [Release Strategy](../ReleaseStrategy) · [Platform Versioning](../PlatformVersioning) ·
[Pinned Image Retention](../PinnedImageRetention) · [Release Support Policy](../SupportPolicy).
