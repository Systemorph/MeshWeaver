---
Name: Two Files, One Node Path
Category: Architecture
Description: The importers derive a node's path from the FILE, never from its content — so Foo.md and Foo.json are ONE node, and the import keeps whichever it wrote last without a word. The rule each importer applies, the guard every node repo's validate lane runs, and the one pair the file-system adapter merges on purpose.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3v4a1 1 0 0 0 1 1h4"/><path d="M17 21H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h7l5 5v11a2 2 0 0 1-2 2z"/><path d="m9 13 6 4"/><path d="m15 13-6 4"/></svg>
---

# Two Files, One Node Path

**A node's path comes from where its FILE sits, not from what the file says.** The package
installer (`PackageInstaller.NodePathForFile`), GitSync (`GitHubSyncService.ParseFile`) and the
file-system adapter all map a repo-relative file through `NodeFileMapper.FromRelativePath`: the
extension is stripped, `X/index.*` folds onto `X`, and mesh paths compare case-insensitively. So
each of these pairs is ONE node:

| Files | Node path |
|---|---|
| `Hosting/StuckDetector.md` + `Hosting/StuckDetector.json` | `Hosting/StuckDetector` |
| `A.md` + `A/index.json` | `A` |
| `Foo.md` + `foo.cs` | `Foo` |

**Nothing refuses the pair at import.** The bulk writer treats a duplicate path as last-writer-wins
(`StaticRepoImporter`, `seenInStage`), and which file is written last depends on enumeration order —
so one author's node silently replaces another's: no error, no log line, nothing to grep.

## The rule the guard mirrors

`.github/scripts/check-node-path-collisions.py` applies the importers' own rule, file for file:

- **Node-file extensions** are `.md`, `.cs` and `.json`, case-insensitive — the built-in parsers of
  `FileFormatParserRegistry`. Every parser MeshWeaver.Plugins contributes (agent, skill, slide)
  claims `.md` only; a module that contributes a NEW extension must add it to the guard too.
- **A `.json` is a node only when it looks like one** — an object carrying `$type`, `id` or
  `nodeType` (`JsonFileParser.LooksLikeMeshNode`). A package manifest beside a node never collides.
  A `.json` that is not strict JSON is not compared — but it is NAMED on a warning line, because
  the hub's serializer options may accept a comment-bearing file this parser refuses, and a
  same-stem sibling of one would then be exactly the silent collision this guard exists for.
- **Not a node by design** (`PackageInstaller.IsNotANodeFile`): the tree-root `README.md`, any
  `manifest.lock`, every file under a `content/` segment (an asset, `ContentAssetMapper`), and a node
  repo's top-level `src/` (module sources).

Every colliding group is reported with ALL its files, and an empty scan is red — a gate over nothing
passes vacuously. The self-test proves each half of the rule can fail: 19 cases, and each of eight
mutants of the rule (no extension strip, no case fold, no `index` fold, no content filter, every
`.json` a node, `src/` scanned, the file-system pair admitted everywhere, the vacuity line removed)
reds it.

## Where it runs

- **Every node repo**, through the shared validate lane (`node-repo-validate.yml`), fetched at the
  lane's scripts ref like every other central guard: `--root .`, the whole Git-visible tree except
  top-level dot-directories and `src/`. That is deliberately WIDER than the package enumeration —
  the canonical `gen-manifests.py --list-packages` refuses a repo without a
  `gen-manifests.config.json`, and two lane callers have none. Over-inclusion can only add files to
  compare; measured over every caller's main it found no group outside a package.
- **Core**, in `CI's own shell`: the self-test, then the doc tree and the samples tree.

## The one pair that is merged on purpose

`FileSystemStorageAdapter.MergeIndexMarkdownAsync` reads `X.json` and lays `X/index.md` in as its
content — the "JSON registry + index.md split". Core's samples tree uses it (`ACME.json` +
`ACME/index.md`), and `stage-samples-gate.sh` removes the `index.md` before a gate install for that
reason. Only that adapter merges; the installer and GitSync do not. So the pair is admitted only
with `--filesystem-layout`, only for a `--tree`, and only as exactly those two files — the same pair
in a node repo is an ordinary collision.

## Measured when the guard landed

Every caller's default branch plus core: 19,569 files, 16,353 node candidates. One group, core's
`samples/Graph/Data/ACME.json` + `ACME/index.md` — the adapter's merged pair above. No collision in
MeshWeaver.Plugins, .Education, .Reinsurance, .SocialMedia, .Manufacturing, .Crm or Memex.
