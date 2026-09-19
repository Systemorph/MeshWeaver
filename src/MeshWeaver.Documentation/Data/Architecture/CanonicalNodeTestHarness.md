---
Name: The Canonical Node-Test Harness
Category: Architecture
Description: run-node-tests.py has one home in core and the satellites launch it — why three vendored copies broke silently and differently the day the compile model changed, and the two controls that make it visible next time.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>
---

# The Canonical Node-Test Harness

`run-node-tests.py` executes a module's in-node tests locally, without booting a mesh. It is the
command every node repository's `AGENTS.md` tells an author to run before pushing, and it is the only
thing between a node repository and *"the C# compiled, so it must be right"* — the compile gate proves
a NodeType builds, never that it is correct. During the Ifrs17 port every genuine defect compiled
perfectly green and returned silently wrong numbers.

It lives in **one** place: `.github/scripts/run-node-tests.py`, in this repository. A satellite keeps
a **launcher** — a file with one function, which fetches the canonical and execs it.

This page is about why that is not a tidiness preference.

## What happened

The harness **imports `compile-check.py`** — deliberately, and the reuse is the point. Both have to
agree on what a NodeType *contains* (its resolved source set) and on what the mesh *compiles* (one
concatenated unit with the `using` directives hoisted). Two implementations of either question drift,
and a harness that compiles something the gate does not is an instrument that reports defects which
do not exist and hides the ones that do.

`compile-check.py` is a canonical, fetched at a moving ref. The harness was not: it existed as three
vendored copies, in MeshWeaver.Crm, MeshWeaver.Reinsurance and MeshWeaver.Plugins, with nothing in
core to compare them against and no guard over any of them.

On 2026-09-18, core `ce104c872e` — *"compile-check builds ONE unit, as the mesh does"* — replaced the
compilation model and removed `usings_union` with it:

| | before | after |
|---|---|---|
| shape | the files compiled as-is, with a `GlobalUsings.cs` added **alongside** | the pieces concatenated into **one unit**, the `using` directives **hoisted** to the top |
| a `using` is | **copied** into the prelude | **moved**, which is what the mesh does |
| the helper | `usings_union(cs_files, ai_available)` | `extract_using_statements(combined_lines(pieces))` |

The three copies broke the same hour, differently, and **no CI lane in the fleet went red** — every
lane runs `compile-check.py` directly, so nothing anywhere invoked the caller:

- **MeshWeaver.Reinsurance** — `AttributeError: module 'compile_check' has no attribute
  'usings_union'`, raised before a single test ran. Its `--self-test` printed `✓ self-test green` in
  the same checkout, because it only ever exercised the collision detector.
- **MeshWeaver.Crm** — patched with a locally re-derived global-usings union behind a
  `hasattr(cc, …)` guard, so it kept *running*, green, against a compile model the gate had
  abandoned.
- **MeshWeaver.Plugins** — alive only because it loads a vendored `compile-check.py` **fork** that
  still carried the removed helper, while that repo's own PR gate runs core's canonical.

Three vintages of one script, three different silent failures, and the loop an author is told to run
before pushing could not start in two repositories for a day. That is the `gen-manifests.py` story
(five vintages, each fix landing in one of them) and the `resolve-platform.py` story (70 re-copy
commits across six repositories, two complete waves obsolete inside ninety minutes) told a third time.

## 🚨 The fix is not the revert

Restoring `usings_union` makes the script *start*. It does not make it right, and the difference is
measurable.

The old union was a **copy**: each file kept its own directives and a `GlobalUsings.cs` was added
beside them. A duplicated namespace import is legal C#; a duplicated `using X = Y;` **alias** is
CS1537. So `_directive_parts` returned `None` for every alias — the union dropped them — and a sibling
file naming that alias failed CS0246 on a name the mesh resolves. The removed docstring said so:

> its hoist is a MOVE. Here the files are compiled as-is and `GlobalUsings.cs` is added ALONGSIDE
> them, so a hoist is a COPY.

Measured on 2026-09-19 against a real NodeType — `LossModelling/Distributions` in
MeshWeaver.Reinsurance, with one fixture file carrying `using System.Text.Json;` and
`using J = System.Text.Json.Nodes.JsonObject;` and a *second* file importing nothing and naming both:

| model | plain sibling `using` | sibling `using` ALIAS | outcome |
|---|---|---|---|
| the canonical, calling `compile-check.py`'s `build_unit` | ✅ | ✅ | 9 tests passed |
| `compile-check.py --modules LossModelling` (the gate) | ✅ | ✅ | `OK LossModelling/Distributions` |
| `usings_union` restored verbatim from `ce104c872e^` | ✅ | ❌ `CS0246: … 'J' …` | 0 tests ran |
| one unit with the hoist removed | ❌ `CS0103`/`CS0246` | ❌ `CS0246` | 0 tests ran |

The one-line revert is row three: it starts, and then refuses a NodeType the mesh and the gate both
compile clean. Which is why the harness now calls `build_unit` rather than shaping anything itself —
"the harness and the gate cannot diverge" is then a property of the code, not of two implementations
that happen to agree today.

## The two controls

A guard nobody runs rots exactly like the thing it guards, so both halves of this are checked where
the change is *made*.

**1. The canonical's `--self-test` runs in core's own CI**, in the same job as
`compile-check.py --self-test`. It loads `compile-check.py`, asserts the functions it needs are still
there, and asserts the hoist textually — a sibling's plain `using`, an alias and a `using static` each
land in the import block and are *gone from the body* (the move, not a copy), while `using var` stays
a statement. Each positive case is paired with a negative control, so it is sensitive to its input
rather than always true. It needs no reference set and takes under a second.

Measured falsifiable, 2026-09-19 — four perturbations of the canonical, each reddening exactly the
cases it should:

| perturbation | cases red |
|---|---|
| re-derive the shaping locally as a global-usings copy | 6 — provenance, both MOVE cases, the alias |
| rename `build_unit` out from under it (what `ce104c872e` did to `usings_union`) | 1, naming the missing symbol |
| revert to one `<Compile>` per source file | 1 |
| keep the hoist but copy rather than move | 3 |

**2. `check-node-test-launcher.py` refuses the copy coming back.** It runs in the shared
`node-repo-validate` lane, so it sees every satellite. A launcher is defined by what it does *not*
contain: no definition of the harness's own functions, no csproj or C# runner template, at most a
handful of top-level definitions, and it must name the canonical. It **discovers** its subject rather
than assuming a path — the three copies did not agree on a location (`scripts/` in two repositories,
`devtools/` in the third), and a guard hard-coded to `scripts/` would have printed *"nothing to
check"* for the one repository whose copy was still running against the abandoned model. A repository
with no copy at all passes with a notice: that is an end state, not a gap.

## How a satellite reaches it

`MW_PLATFORM_SCRIPTS` (a core checkout's `.github/scripts`) always wins — that is the offline route
and the way to pin the harness to an exact vintage. Otherwise:

- **MeshWeaver.Crm, MeshWeaver.Reinsurance** — `scripts/platform-script.py`, the loader those
  repositories already use for `compile-check.py`. In Crm, `LANE_FOR` maps `run-node-tests.py` to
  `node-repo-compile-check.yml` — **the same lane as `compile-check.py`, deliberately**, so one ref
  governs both and the harness cannot be a different vintage from the unit shaping it compiles.
- **MeshWeaver.Plugins** — its own launcher fetches *both* scripts into one cache outside the
  repository, the shape `scripts/fetch-canonical-resolver.py` already uses for the resolver. The
  harness therefore loads **core's** `compile-check.py`, which is what that repo's PR gate runs. Its
  vendored `scripts/compile-check.py` stays as the documented local compile command; that copy's own
  drift from the canonical is a separate, still-open question.

The canonical resolves the repository under test from `MW_REPO_ROOT`, falling back to the working
directory — never from its own location, because its own location is a cache directory with no node
content in it.

## What the canonical does that two of the copies did not

The canonical is MeshWeaver.Reinsurance's shape, which is the one that matches the mesh:

**One compilation per NODETYPE, never one merged compile per module.** The mesh compiles each
NodeType alone, so a package may deliberately duplicate a shared file across several types'
`Source/` folders and each type then sees exactly one definition. A harness that concatenates every
`Source/` folder in a package is compiling a program that exists nowhere, and it dies on the
duplicates: `run-node-tests.py UWDeepfield` used to fail with ~150 `CS0101`/`CS0111` errors before a
single test ran, so **46 NodeTypes' suites had never executed once** — invisibly, because
`compile-check.py` compiles per type and stayed green throughout.

Adopting it changed what MeshWeaver.Crm reports, and the change is arithmetic rather than coverage:
the same **13 suites and 153 distinct case names** as before, executed **481 times over 7 distinct
source sets** instead of 163 times over one merged program no NodeType is. A suite shared by several
types runs once per set, which is what the mesh does to it.

It also names, rather than drops, what it could not run: NodeTypes whose sources collide inside their
own set, `.cs` files under a `Source/` or `Test/` folder that **no** NodeType declares (the mesh never
compiles those either), types carrying no `*Tests` class, and sets that need the
`Microsoft.Extensions.AI` assemblies the local reference set does not carry — reported UNVERIFIABLE,
because calling a missing mesh-supplied assembly a broken NodeType sends the reader off to fix source
that was never broken. And every build diagnostic carries the **authored file and line** it came from,
mapped back through the unit's origin map, instead of a line number in a generated `Combined.cs`.

## See also

- [Reading CI Signals](../ReadingCiSignals) — why a green wall is not a covered change
- [NodeType Compilation](../NodeTypeCompilation) — what the mesh compiles, and when
- [Module Build Architecture](../ModuleBuildArchitecture) — one build shape, every repository
