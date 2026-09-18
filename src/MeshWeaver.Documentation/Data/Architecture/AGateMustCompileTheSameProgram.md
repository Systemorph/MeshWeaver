---
Name: A Gate Must Compile the Same Program
Category: Architecture
Description: The pre-push NodeType gate handed each source file to MSBuild as its own Compile item while the mesh concatenates them into one unit, so a nullable-context directive in the first file — which is in force in the last — reached nothing, and the gate was structurally blind to diagnostics the bake then reported under the NodeType's name with no file and no line. What the unit boundary decides, the culture-sensitive StartsWith that a naive reproduction gets wrong, and why the two shapings are now compared against each other rather than kept in step by hand.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 5h18"/><path d="M3 12h18"/><path d="M3 19h18"/><path d="M8 5v14"/><circle cx="16" cy="12" r="2.5"/></svg>
---

# A Gate Must Compile the Same Program

`.github/scripts/compile-check.py` is the gate every node repo runs on a pull request and every
agent runs before pushing. Its job is to answer one question — *will the portal compile this?* — and
until Systemorph/MeshWeaver#4711 it answered a **different** question, in a way that could only ever
err towards green.

It handed each of a NodeType's source files to MSBuild as its own `<Compile>` item. The mesh does
not: `DynamicMeshNodeAttributeGenerator.GenerateAttributeSource` takes the concatenation of the
whole source set (`NodeCompileShaping.CombineSources` — joined with a blank line, in node-path
order), strips every file's `using` lines, and re-emits them deduped at the top of **one** generated
file. Both are legal C#. Only one of them ships.

## What the unit boundary decides

Several C# constructs are scoped to a **file**, from where they appear to the end of it. In a
concatenation they therefore reach every file that follows:

- `#nullable enable` / `disable` / `restore`
- `#pragma warning disable` / `restore`
- `#define` / `#undef`
- the `using` directives themselves — which is why the generator has to hoist them at all
- a file-scoped `namespace X;`, which swallows everything after it (and a second one is an error)

The one that bites is the first. `EmitPipeline.CreateCompilationOptions` sets
`NullableContextOptions.Annotations`, so **nullable analysis runs only in text that opted in**. In
the concatenated unit a single `#nullable enable` near the top switches the analysis on for the rest
of the compile — including files whose author never asked for it, and including generated code.
File-per-`<Compile>`, it reaches nothing past its own file.

So the divergence was silent and **one-directional**: the gate was clean, the bake was not.

## Why the misattribution followed

The bake reports warnings in aggregate — `CS8602 14× / 1 site(s) / 3 type(s)` names a code and a
count, never a file or a line — and the warning ratchet
([The In-Mesh Warning Standard](/Doc/Architecture/InMeshWarningStandard)) records the pair under the
**NodeType's** path. Nobody who owned that NodeType could reproduce it, because the gate they were
told to run could not see it. `plugin-gate-warnings.allow`'s own header
(*"THIS IS NOT A PLACE TO PUT A DIAGNOSTIC YOU DID NOT WANT TO FIX"*) then read as an accusation
against content that was not at fault.

It happened twice in one file. A `CS0105` row carried the comment *"Authored, not generated: … a
genuine duplicate in this repo's own source"* — it was a UTF-8 byte-order mark the proxy generator
had written mid-file. A `CS8601` row was labelled *"a latent bug"* when it was the same generator.
Both were fixed at the root in MeshWeaver.Plugins#2071; the instrument gap that hid them is what
#4711 closes.

**Measured on MeshWeaver.Plugins/BusinessRules/Scope, 2026-09-18, same tree both ways:**

| instrument | verdict |
|---|---|
| the gate, file per `<Compile>` | `98 clean` |
| the bake (`mw-plugin-test compile`) | `CS8601 12×`, `CS8602 12×` on that one type |

All 24 were one emit in the scope-proxy generator — `typeof(X).GetProperty(nameof(X.Y)).GetMethod`,
a possibly-null dereference **and** a possibly-null assignment, once per property, in each of the
seven committed `Test/Generated/*Proxy.cs`. The proxies carry no `#nullable` directive of their own.
They inherit one from a file that sorts ahead of them in the concatenation.

## The fix is in the gate, never in the mesh

The concatenation is the shipped behaviour. Making the mesh compile file-per-file would change what
actually runs — a far larger act than fixing an instrument. So the gate now builds **one
compilation unit per NodeType**: every source read, combined in node-path order, the `using`
directives hoisted and deduped, and the `configuration` lambda appended last where the generated
`ConfigureHub` sits, so it inherits the same nullable context the mesh gives it.

Three things follow from doing it properly rather than approximately:

**The hoist became a MOVE.** The old shape left each file's directives in place and added a
`GlobalUsings.cs` beside them, so a hoist was a *copy* — and a duplicated `using X = Y;` is CS1537.
Aliases were therefore dropped from the union, leaving a sibling file and the configuration lambda
unable to see an alias the mesh does hoist. With the directives actually removed from the code, an
alias hoists exactly as the mesh hoists it.

**The order became the node-path order.** Sorting file paths agrees with node-path order almost
always and not always — `A/B.cs` sorts before `A/B.Extra.cs` as a file and after it as a node path.
Since the join order decides which `#nullable` reaches which file, "almost always" is not a
reproduction.

**Every diagnostic now names a file and a line.** MSBuild reports against the combined file; the
gate keeps a line → `(node path, line)` map and prints the authored site. That is the other half of
the misattribution: a count against a NodeType is not something an owner can act on.

## The trap a naive reproduction falls into

`ExtractUsingStatements` tests `trimmed.StartsWith("using ")` and `trimmed.EndsWith(";")` through
the **parameterless** overloads. Those are *linguistic*, not ordinal — and a linguistic comparison
gives Unicode **format** characters (category `Cf`: U+FEFF, U+200B–U+200F, U+00AD, U+2060 …) no
collation weight at all. `Trim()` does not remove them, because they are not whitespace to
`char.IsWhiteSpace`. So they survive into `trimmed`, and the comparison matches straight through
them.

Measured against the generator, 2026-09-18, for each of U+FEFF, U+200B, U+200E, U+00AD and U+2060 in
front of a `using` line:

| | culture-sensitive `StartsWith("using ")` | ordinal | hoisted? | emitted as |
|---|---|---|---|---|
| no prefix | `true` | `true` | yes | `using System.Text;` |
| `U+FEFF` (and the other `Cf`) | **`true`** | `false` | **yes** | `<U+FEFF>using System.Text;` — verbatim |
| `U+0009`, `U+0085`, `U+00A0` | `true` | `true` (trimmed away first) | yes | `using System.Text;` |

Python's `str.startswith` is ordinal and has no such overload. A reproduction that uses it leaves the
directive in the code, after other declarations, and reports a **CS1529** the portal never produces
— a false red invented by the gate, on content that is fine. That is not hypothetical: seven
committed scope proxies carried exactly that mid-file mark, and the first cut of this fix reported
it as a break.

The gate therefore strips `Cf` characters **for the comparison only**, and emits the directive
verbatim — including the character — because that is what `userUsing.Trim()` leaves and what the
ordinal dedup then keys on.

## Why the two shapings are compared, not maintained in parallel

`DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource` exists as a separate member so the script can
be pinned to it. `ConcatenatedUnitParityTest` (test/MeshWeaver.Compiler.Pipeline.Test) writes a
deliberately nasty fixture — a `#nullable` crossing a file boundary, a duplicate import, an alias, a
`using static`, a `using var` statement, a `using` line inside a block comment, a leading byte-order
mark and three mid-file format characters — runs `compile-check.py --emit-unit` over it, and requires
the script's import block and stripped code to equal the generator's **character for character**.

That is the same reasoning `ModulePlatformFloorScriptParityTest` is built on: two call sites
computing one fold differently either never converge or never fire, and both are silent. It is not decoration
— **the culture-sensitive comparison above was found by this test failing**, before the change
landed, on a reproduction that looked obviously correct.

The fixture asserts its own inputs (the script exists, `python3` runs it, the fixture really does
carry each shape), because a parity test that skips, or that compares two renderings of nothing,
passes having checked nothing at exactly the moment its subject is unreachable.

## What the corrected gate newly sees

Measured against `Systemorph/MeshWeaver.Plugins` at `main` (`e09554a`), 99 NodeTypes, the platform
image's own reference set, both shapes in the same run:

- **In CI's default mode — the one every node repo actually runs — nothing changes: 0 → 0.**
  Warnings are not errors there and the nullable codes are on `NoWarn`, so the corrected unit
  boundary moves no verdict.
- **Under `--warnings-as-errors`, 2 diagnostics appear, on 2 of 99 NodeTypes**, both `CS8602`
  (`Edu/CourseCatalog/Test/CourseCatalogTests.cs`, `Store/Plugin/Source/PluginLayoutAreas.cs`).
  Both files declare no `#nullable` of their own and inherit one from a file in **another module**
  five files earlier in the concatenation — `Edu/AnswerSheet/Source/AnswerHarvest.cs` in the first
  case, which `Edu/CourseCatalog` pulls in through a `shared=@…` source declaration.

Both are real diagnostics that the mesh's own compile produces; the gate and the bake now agree
about them. Whether an authored file *should* inherit a nullable context from an unrelated module is
a separate question about the concatenation itself, and changing that would change what ships.

One class of finding is fixed rather than reported, because it was never the content's to fix: the
byte-order mark. `File.ReadAllText` strips a leading one, so `CodeConfiguration.Code` never carries
one, and the gate reads each file with `utf-8-sig` for the same reason. Plain `utf-8` keeps U+FEFF as
the file's first *character* — harmless while each file was its own unit, and not harmless once
every file after the first lands in the middle of one.

## Related

- [NodeType Compilation & Releases](/Doc/Architecture/NodeTypeCompilation) — the runtime side of the
  same compile
- [The In-Mesh Warning Standard](/Doc/Architecture/InMeshWarningStandard) — the ratchet that was
  recording these under the wrong owner
- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — where the gate sits in the
  one build shape
