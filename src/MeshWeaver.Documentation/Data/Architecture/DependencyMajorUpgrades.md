---
Name: Dependency Major Upgrades
Category: Architecture
Description: How to cross a dependency's MAJOR version boundary here — the four things a green -warnaserror build cannot see, the differential method that replaces them, and the ledger of boundaries we have actually crossed.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="m15.5 5.5-3.5 3-3.5-3"/><path d="M5 12h14"/><path d="M7 12v4a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2v-4"/><circle cx="12" cy="21" r="1"/></svg>
---

# Dependency Major Upgrades

The standing directive is to run the **latest** version of every library, at minimum current within
the minor line. A **major** boundary is the exception that needs a decision and a method, because it
is the one place where the compiler stops being a witness.

This page is the method and the ledger. [Dependency Licensing](../DependencyLicensing) covers which
licences may be adopted at all; this page covers how to cross a boundary once the licence is clear.

## Why a major is different here

A clean `dotnet build -c Release -warnaserror` is the normal standard of proof in this repository.
For a major bump it is close to worthless, for four independent reasons:

| What a green build cannot see | Why |
|---|---|
| **In-mesh source** | Every `.cs` stored in a mesh node — NodeType `Source/*.cs`, Scripts, layout areas — compiles at RUNTIME in the portal. A NodeType's `configuration` lambda is C# inside a JSON string, invisible even to `grep --include='*.cs'`. |
| **Authored CONTENT** | A parser bump changes how *data already in the tree* is read. Nothing compiles that data, so nothing fails when it starts parsing differently. |
| **The satellite repositories** | `MeshWeaver.Plugins` has **no `Directory.Packages.props` of its own**. Its `src/Directory.Packages.props` IMPORTS core's, at the commit named by `MW_PLATFORM_REF`. A one-line edit here therefore chooses what *another repository* compiles against — and core CI is green by construction, because nothing here references what broke. |
| **Behaviour behind an unchanged signature** | The shape no surface detector can see. The method below attacks it with a corpus, not with a diff. |

🚨 The `Satellite package pins (removal declared)` gate does **not** cover this. It fires on a
`PackageVersion` being **removed**, not bumped — a bump keeps the `Include` and so never trips it.
The satellite reads a bump only when somebody moves its pin, on another day, in another pull
request.

## The method

Five steps, in order. Each produces a positive artefact; none of them can pass by returning nothing.

### 1. Measure the premise against the live registry

Never adopt a target version asserted by an earlier session, an issue, or a memory. Ask nuget.org,
and **gate the read on HTTP status AND byte count** — a failed request's empty body greps to zero
and reads exactly like "no such version".

🚨 **PRINTING the status and the byte count is not GATING on them.** Plain `curl -sS -o file -w …`
exits **0** on an HTTP 404, writes the error body into the output file, and prints a tidy
`HTTP=404 BYTES=14` that a hurried reader skims as evidence — this repository's own "a verification
step that cannot fail is not a verification step" rule, broken in the one place it is being taught.
Make the command *refuse*:

```bash
curl -sS --fail-with-body -o idx.json \
     -w "HTTP=%{http_code} BYTES=%{size_download}\n" \
     "https://api.nuget.org/v3-flatcontainer/<package-id-lowercased>/index.json" \
  || { echo "registry read FAILED — premise unmeasured, do not proceed"; exit 1; }
[ -s idx.json ] || { echo "registry read returned an EMPTY body — do not proceed"; exit 1; }
```

`--fail-with-body` turns a 4xx/5xx into a non-zero exit while still keeping the body to look at, and
the `-s` test refuses a zero-byte success.

### 2. Read the licence at the NEW major, not at the old one

A licence can change **at** a major boundary — that is exactly how `JsonPatch.Net` 5.0.0 moved
json-everything's published binaries to a maintenance fee, which is why the Aspire family is pinned.
Compare the `<license>` element of both nuspecs; a change is a **stop-and-report**, never a call to
make alone.

```bash
curl -sS --fail-with-body -o new.nuspec \
     -w "HTTP=%{http_code} BYTES=%{size_download}\n" \
     "https://api.nuget.org/v3-flatcontainer/<id>/<new>/<id>.nuspec" \
  || { echo "nuspec read FAILED — licence unread, do not proceed"; exit 1; }
[ -s new.nuspec ] || { echo "empty nuspec — do not proceed"; exit 1; }
```

🚨 The same trap as step 1, and worse here: an unguarded `grep '<license'` over a 404 body finds
nothing, and "no licence element" is easy to misread as "no licence change".

### 3. Diff the real public API, don't read the release notes alone

Release notes are a summary written by someone who was not thinking about this codebase. Download
both `.nupkg`s, extract the assembly for the TFM we actually resolve, and enumerate the public
surface of each with `MetadataLoadContext`. Sort, normalise away the assembly-qualified version
stamps (they otherwise dominate the diff), and `comm` the two lists.

The **removal** set matters first: check it against what this repository and the satellites actually
call.

🚨 **But "additions cannot break a consumer" is FALSE, and believing it is how a major audit misses
an entire break class.** Additions cannot break a **caller**. They routinely break an
**implementer**: a member added to an interface with no default implementation, or an `abstract`
member added to an abstract class, breaks every outside type that implements it — `CS0535` at the
dependent's next pin move, long after this repository went green. The YamlDotNet entry below is an
instance: 18.0.0 added `HasParseMethod` and `Parse` to `ITypeInspector` with no default
implementation, and that is the single declared breaking change of that major. It missed us only
because neither repository implements the interface.

So the addition set is **not** dismissible — partition it. Additions to *static* types, to sealed
types, and new overloads are caller-only and safe to skim. Additions of abstract or default-less
members to a type an outside assembly may implement are **removals in disguise**, and get the same
treatment: enumerate them, then search both repositories for implementers. (Core carries the
mirror-image obligation on its own surface — the `Interface additions (implementers declared)` gate —
for exactly this reason.) One more caller-side trap an addition can cause here: a new overload can
make a dependent's `<see cref>` ambiguous, `CS0419` under `-warnaserror`.

### 4. Run the change against the CORPUS, not only the suite

This is the step that finds behaviour changes behind unchanged signatures, and it is the one most
often skipped.

Build the *same* probe program twice — identical source, two project files differing only in the
pinned version — configured **exactly** as production configures the library. Run both over every
artefact in the tree the library actually processes, and emit one stable, hashed line per artefact.
Then diff the two outputs. A zero-line diff over a few thousand real inputs is a far stronger
statement than any suite.

🚨 **Control the instrument before believing its zero.** A probe that cannot report a difference
reports zero for the wrong reason. Two checks, both cheap:

- **Negative control** — count distinct hashes. If the corpus collapses to a handful, the hash is
  not discriminating and the zero is vacuous.
- **Positive control** — mutate one cell of one output file and re-run the diff. It must fire.

### 5. Build and run the full suites, and never re-run a red

Build every touched project and its dependents `-c Release -warnaserror`, one project per
invocation, and read a literal `0 Error(s)` plus an elapsed time that makes sense — a two-second
"Build succeeded" is an up-to-date no-op, not a build. Confirm the new assembly actually landed next
to the test binary (check the copied DLL's version), because a props edit that did not take reads
identically to one that did.

Then run the **full** suites, not a filter. A major bump is exactly where a filtered run lies. And a
red after a major bump is a real behavioural change until proven otherwise — that is the entire
point of the exercise, so it is investigated, never re-run.

## Ledger

### YamlDotNet 16.3.0 → 18.1.0 — two majors, zero adaptation

Measured 2026-09-11.

| Step | Result |
|---|---|
| Registry | `…/v3-flatcontainer/yamldotnet/index.json` → HTTP **200**, 5,860 B. Newest stable **18.1.0**; 270 versions listed. |
| Licence | **MIT at both ends** — `<license type="expression">MIT</license>` in the 16.3.0 and 18.1.0 nuspecs alike. No change. |
| API removals | **6 public members** across both majors: `Accepts(Type)` on the five built-in date/time converters (refactored onto the new `ScalarConverterBase<T>`, so still present by inheritance), and `YamlDotNet.Core.Tokens.Scalar.IsKey`. `IYamlTypeConverter` is **byte-identical** between the two. |
| Our usage | Six symbols total — `DeserializerBuilder`, `SerializerBuilder`, `IDeserializer`, `ISerializer`, `DefaultValuesHandling`, `YamlException` — all present in both. **Zero** custom `ITypeInspector`, `IYamlTypeConverter`, `INodeDeserializer` or `TypeInspectorSkeleton` implementations in either repository. |
| Corpus | **1,817 front matters** across core and MeshWeaver.Plugins parsed and re-serialised by both versions: **0 differing lines**. 1,799 parsed OK with identical value hashes and identical re-emit hashes; the same 18 files raised the same `SemanticErrorException` on both. |
| Suites | `MeshWeaver.Documentation.Test` 390 passed / 0 failed; `MeshWeaver.Hosting.Test` full suite green. |

**The two declared breaking changes both miss us, and it is worth recording why** — each would have
been invisible in a build:

- **18.0.0 added two members (`HasParseMethod`, `Parse`) to `ITypeInspector`** with no default
  implementation. That breaks *implementers*, not callers. Neither repository implements the
  interface, so nothing had to change — but a satellite that grew a custom type inspector would
  break at its next pin move, not here.
- **18.1.0 introduced a default maximum YAML nesting depth of 130.** Node front matter is a flat map
  of scalars plus the occasional string list — depth 2 to 3 — so the ceiling is two orders of
  magnitude away. It is a real behaviour change with a real (if distant) failure mode: a document
  deeper than 130 now throws where it previously parsed. `DeserializerBuilder.WithMaximumRecursion(n)`
  is the lever if that day ever comes.

One adjacent change was checked and does not reach us: **17.1.0** gave `MergingParser` a default cap
of 100k events. Neither repository constructs a `MergingParser`.

**What this bump changes for MeshWeaver.Plugins.** `src/MeshWeaver.AI` (skill and agent front
matter) and `src/MeshWeaver.Publish` (slide front matter) carry versionless `YamlDotNet` references
resolved from core's list, so both move to 18.1.0 when the satellite's `MW_PLATFORM_REF` next moves.
Both were included in the 1,817-file corpus run above, and neither implements an extension point
that 18.0.0 broke.
