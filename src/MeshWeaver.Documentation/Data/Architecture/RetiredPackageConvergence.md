---
NodeType: Markdown
Name: "Retired Package Convergence"
Abstract: "How an instance converges away a package the plugin repository retired: the registry DECLARES retirements (retired-packages.json, served as the listing's retired set), a package merely absent from a listing is never a removal, a proven retirement is skipped quietly by the compile watcher (one Information per boot, no Error park) and its install record is removed through phase 1 of the one uninstall request, with the partition's data left in place."
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Plugins"
  - "Packages"
  - "Registry"
---

# Retired Package Convergence

MeshWeaver#8619. A package the plugin repository retired (a module that was deleted from the repository)
leaves two things behind on every instance that had it installed: its **install record** (a `Package` node
under the install-record partition, naming the retired module) and its **partition** with the types in it.
Until this page's mechanism existed, every pod start then parked each of the retired package's NodeTypes with
an Error, because no bundle can carry a package that no longer exists, and the release gate could only say
"if the package is genuinely retired, remove its install record".

## The rule: retirement is DECLARED, never derived

Policy `declared-not-derived`: **an absent contributor is not a removal.** A package missing from one
registry listing proves nothing, because an outage, a partial listing, a source the caller is not granted and
a retired package all look identical there. A package counts as retired only when the registry says so
explicitly.

| Layer | What it does |
|---|---|
| Source repository | Commits `retired-packages.json` at the repository root: `{"retired":[{"id":"ContainerRegistry","reason":"…","retiredAt":"2026-09-08"}]}`. An entry without a valid id is skipped; a malformed file declares nothing. |
| `IPackageSource.ListRetired` | The source reads that file (`NodeRepoPackageSource.ListRetired`), cached with the listing's stale-while-revalidate cache. The default for a source that declares nothing is the empty list. |
| Registry endpoint | Serves the declarations beside the listing: `{ "packages": […], "retired": [{ "id", "source", "reason", "retiredAt" }] }`. The member is absent when nothing is retired, so the bytes are the old ones. An old consumer ignores it; an old registry never writes it. |
| `RegistryListing.Retired` | The consumer's reading. |

## What a proven retirement is

`RetiredPackageConvergence.Proven(source, listing)` returns the entries of a SUCCESSFUL listing's `retired`
set, minus any id the same listing also lists as a package (a re-introduced package: presence wins, the safe
direction). Nothing else is proof. A listing that failed, a source held at boot, a source never listed: no
entry, nothing proven.

## What the instance then does

1. **Declares it to the compile control plane** (`IRetiredPackages`, an instance-scoped ledger). The compile
   watcher's untracked-module gate asks it before it parks: a type of a retired package settles WITHOUT Roslyn
   and WITHOUT the Error park (`NodeTypeCompileParkRegistry` is never told), and the watcher logs **one
   Information per partition per boot** naming the package and the reason. The check runs both before and
   after the gate's adoption wait, because the registry listing may be read while that wait runs.
2. **Removes the install record** through phase 1 of the one package-uninstall request, as System
   (`DeclaredDisabledRetirement.Retire`, with the proven set expressed as source-scoped retired patterns
   under the flag name `registry-retired`): the module binding and the install record go, the **partition's
   content stays**, and phase 2 can never follow because the request names no requester. A retired package
   that another installed package requires is refused by name, exactly as a flag retirement is. The same
   request key makes every replica's boot decide the same attempt, so there is one request.

The pass runs at the boot default install, after the selection read the listing. It never faults and never
withholds the install summary.

## Verifying on a live instance

- Loki: one `Compile watcher: package … is RETIRED by the registry` Information per retired partition per
  boot, and no `PARKING with a named refusal` Error for its types.
- The retired package's install record is gone; its partition's nodes are still readable.
- The retired id is present in the registry listing's `retired` member, and absent from `packages`.

## Retiring a package (for authors)

Delete the package folder and add its entry to `retired-packages.json` **in the same pull request**. The
declaration is what instances act on; deleting the folder alone retires nothing anywhere (and it must not).

## Tests

`RetiredPackageProofTest`, `ARegistryRetiredPackageIsConvergedAwayTest` (`MeshWeaver.Hosting.Test`) and
`ARetiredPackageIsSkippedQuietlyTest` (`MeshWeaver.Compiler.Pipeline.Test`): retired-in-listing removes the
record and keeps the data; absent-from-a-partial-listing removes nothing (the negative control); an
undeclared leftover still parks.

Related: [Package Uninstall](../PackageUninstall), [Environment Composition](../EnvironmentComposition)
(a flag declared off retires what is installed), [GitHub Sync](../GitHubSync) (a retired package is not synced).
