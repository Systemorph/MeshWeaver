# Dependency Licensing

MeshWeaver is open source, dual-licensed **Apache-2.0 / MIT**, and is distributed
both as NuGet packages and as a network-served product. Two families of
dependency licence are therefore incompatible with shipping it, and adding one is
a **defect**, not a preference:

| Family | Why it is incompatible |
|---|---|
| **Copyleft** — AGPL, GPL, LGPL | Viral. AGPL in particular reaches a network-served product: serving the portal over HTTP is "conveying" for AGPL purposes, so it would attempt to impose its terms on MeshWeaver itself. |
| **Pay-to-use** — a "community" tier that becomes a paid licence above a revenue threshold, or a fee attached to the published binary | An open-source product cannot carry a dependency its users must pay to run. |

## Why this needs a gate

A licence is added by **one line** in `Directory.Packages.props`. The compiler
never complains, no test fails, and no other check in CI looks at licence
metadata. A licensing problem is therefore completely silent — it can sit in the
tree indefinitely and first surface in a legal review.

It already had. An audit in August 2026 found an **AGPL-licensed `itext7`
declaration** in central package management. It had never been resolved into any
project's dependency graph, so nothing shipped with it — but nothing in the build
would have said so either way, and the same line with a `PackageReference`
beside it would have shipped AGPL code into every portal image.

## The gate

`.github/scripts/check-licenses.py`, run by the **`Dependency licences`** job in
`dotnet-test.yml`. It:

1. restores the solution, then reads every package in the **restored dependency
   graph** from the `project.assets.json` files — **direct and transitive**,
   because a copyleft library that arrives indirectly binds the product exactly
   as hard as one we chose ourselves;
2. resolves each package's licence from the `.nuspec` in the NuGet cache — the
   SPDX `<license>` expression where present, otherwise by classifying the
   licence **file** the package ships;
3. fails RED, naming each offending package and its licence.

### What counts as a violation

Only packages that **actually ship**. A `<PackageVersion>` line that no project
references restores nothing, is downloaded by nobody, and is redistributed in no
image — it is dead configuration, reported but not failed on. (`itext7` was
exactly this.) The audit found ~41 such dead declarations.

### The allowlist

Permissive only: `MIT`, `MIT-0`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause`,
`ISC`, `0BSD`, `Unlicense`, `MS-PL`, `PostgreSQL`, public domain, and the
Microsoft .NET Library licence. An SPDX `OR` expression passes when any disjunct
is allowed (e.g. `MS-PL OR Apache-2.0`).

**Adding an entry to the allowlist is a licensing decision — never a way to make
a build green.**

### Classifying licence files

Many packages ship a licence *file* instead of an SPDX expression. The classifier
tests **disqualifying patterns first**: a licence file that grants MIT-style
permissions *and* mentions the Affero GPL is classified AGPL, not MIT. This
ordering matters — dual-licensed and relicensed packages routinely contain the
permissive text alongside the restrictive terms, and matching the permissive text
first is how a copyleft dependency slips through.

### Exceptions

`EXCEPTIONS` in the script names packages allowed despite a non-allowlisted or
unresolvable result. **Every entry carries a written reason**, and there are only
two legitimate kinds:

- **The upstream licence genuinely is permissive and only the package metadata is
  missing** — e.g. the `UglyToad.PdfPig` custom build ships no `<license>`
  element while upstream is Apache-2.0.
- **Tracked debt**: a known-incompatible package whose removal is in flight, with
  the issue that removes it. An exception of this kind is **not an approval**, and
  a passing run still prints it so it cannot quietly become permanent.

## Gate shape

Per the repo's no-skip-trapdoor rule (`AGENTS.md` → "A gate NEVER tests its own
inputs"), this gate:

- carries **no `continue-on-error`** and **no input-shaped `if:`**. It reads only
  the repo and the public NuGet feed, so it needs no secret, has no legitimate
  reason to be skipped, and has **no fork exemption** — it runs on fork PRs too;
- **fails closed on missing evidence**. If no dependency graph is found, or a
  licence cannot be resolved, that is a failure — a gate that passes on absent
  evidence is worse than no gate at all;
- is wired into `collect-results` (the repo's only required status check) with an
  **explicit fail step**, because `needs:` alone does not fail an `always()` job.

## Running it locally

```bash
dotnet restore MeshWeaver.slnx
python3 .github/scripts/check-licenses.py            # the gate
python3 .github/scripts/check-licenses.py --report   # the full table
```

The report marks each package: `X` violation, `~` documented exception, `.`
declared but never restored.
