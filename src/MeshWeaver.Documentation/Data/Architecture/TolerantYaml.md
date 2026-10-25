---
Name: Tolerant YAML
Category: Architecture
Description: "The platform reads YAML with its own reader — YAML syntax, JSON semantics: the text becomes a JSON tree and binds under the same tolerant rules as the platform's JSON node files, a colon-space inside a value is text, and text with no single reading fails with one exception type that names the line and column."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M8 12h12"/><path d="M8 18h12"/><path d="M4 12h.01"/><path d="M4 18h.01"/></svg>
---

# Tolerant YAML

Front matter on agents, skills and doc pages, GitHub workflow files, helm output: the platform reads all of
it with **one** reader of its own, `MeshWeaver.Yaml` (`src/MeshWeaver.Yaml`). It has no third-party
dependency and one rule:

> **YAML syntax, JSON semantics.** The text is parsed into a `System.Text.Json` `JsonNode` tree and then
> bound with System.Text.Json under the same tolerant options the platform uses for its JSON node files.

The previous third-party reader was strict in every direction at once: an unknown key, a casing
mismatch, a number where a string was declared, or a `: ` inside an unquoted value threw. In
`MarkdownFileParser` a throw sent the whole block to a regex rescue that recovers only six single-line
fields, so the node imported with its description and every other key silently missing
(`MarkdownContent.UnboundFrontMatter`, #4319, now names what a typed file lost).

## The surface

| Call | Returns |
|---|---|
| `TolerantYaml.Parse(text, options?)` | the first document as `JsonNode?` (`null` for an empty text) |
| `TolerantYaml.ParseAll(text, options?)` | every `---`-separated document |
| `TolerantYaml.Deserialize<T>(text, jsonOptions?, options?)` | the first document bound to `T`; `default` for an empty one |
| `TolerantYaml.Serialize(node)` / `Serialize<T>(value, jsonOptions?)` | block-style YAML text |
| `TolerantYaml.ScalarText(node)` | a scalar's text as YAML wrote it (`"true"`, `"1.10"`, the string) |
| `TolerantYaml.CreateSerializerOptions()` | a new instance of the tolerant binding options |

The one failure type is `YamlParseException` (a `FormatException`) carrying `Line` and `Column`, both
1-based. A tree that parses but does not bind to `T` throws System.Text.Json's `JsonException`, exactly
as the same JSON would.

## From YAML to a JSON tree

| YAML | JSON tree |
|---|---|
| block or flow mapping | `JsonObject`, keys in document order |
| block or flow sequence | `JsonArray` |
| `~`, `null`, `Null`, `NULL`, an empty value | `null` |
| `true`/`True`/`TRUE`, `false`/`False`/`FALSE` | boolean |
| `42`, `-7`, `007`, `0x1F`, `0o17` | number (`7`, `31`, `15`) |
| `1.10`, `1e3`, `.5` | number, digits kept exactly as written |
| `.inf`, `-.inf`, `.nan` | the strings `Infinity`, `-Infinity`, `NaN` (they bind to `double`) |
| anything else plain, and **every quoted scalar** | string |

This is the YAML **1.2 core schema**: `yes`, `no`, `on`, `off` are strings, and a date such as
`2026-01-01` is a string that binds to `DateTime`/`DateTimeOffset` like its JSON equivalent.

Covered syntax: block mappings and sequences (indented or not under a key, the compact `- key: v` form,
nested `- - a`), flow collections over several lines with trailing commas, plain scalars folded over
several lines, single-quoted (`''`) and double-quoted scalars with every YAML escape (`\n`, `\t`, `\xXX`,
`\uXXXX`, `\UXXXXXXXX`, an escaped line break), literal `|` and folded `>` block scalars with the `-`/`+`
chomping indicators and an explicit indentation digit, full-line and trailing `#` comments, `---` and
`...` document markers, `%` directives (skipped), anchors `&a` and aliases `*a` (an alias is a deep copy),
merge keys `<<: *a` and `<<: [*a, *b]` (explicit keys win, then the first source that names a key), and
the core tags `!!str`, `!!int`, `!!float`, `!!bool`, `!!null`. Any other tag (`!Ref`) is ignored and the
value read as if it were untagged. A CRLF line ending and a leading byte-order mark are normalised away.

Not covered: explicit `? key` entries (refused with a position) and a collection used as a mapping key
inside a flow mapping (read as its JSON text).

## The tolerances

**In the reader.** Two places where malformed YAML has exactly one sensible reading:

- **A `: ` inside a plain value is text.** `description: new deal: fund pilot` reads as the string
  `new deal: fund pilot`. A mapping cannot start in the middle of a value line, so the only other
  reading is a refusal — and the refusal is what used to lose the whole block.
- **A duplicate key keeps its last value**, as System.Text.Json does with a duplicate JSON property.

Everything else that has no single reading — an unterminated quote or flow collection, an unknown
alias, a tab in indentation, an entry indented between two levels, text after a closing quote — throws
`YamlParseException`.

**In the binding** (`CreateSerializerOptions`, the default for `Deserialize<T>`):

| Rule | So that |
|---|---|
| property names case-insensitive, written camelCase | `nodeType:`, `NodeType:` and `NODETYPE:` all bind |
| unknown keys skipped | an author's own metadata never fails a read |
| numbers readable from strings, named float literals | `order: "7"` binds to an `int` |
| any scalar binds to a `string` as its written text | `version: 1.10` gives `"1.10"`, `name: 123` gives `"123"` |
| a `"true"`/`"false"` string binds to a `bool` | quoting a switch does not break it |
| enums by name, case-insensitively | `colour: green` binds to `Colour.Green` |
| nulls omitted on write | a written file carries only what is set |
| a missing key keeps the property's default | as in JSON |

A caller may pass its own `JsonSerializerOptions`; `MarkdownFileParser` writes front matter with
PascalCase keys in declaration order, which keeps every existing mirrored file byte-identical.

## Strict mode — for files strict readers also read

`YamlParseOptions.StrictSpec` refuses the two reader tolerances above, exactly where the YAML grammar
does. The platform's own reads are tolerant; strict mode is for guards over files that OTHER readers
also consume — `SkillFrontMatterGuard` (`.claude/skills`, read by the coding harnesses) and
`DocumentationFrontMatterGuard` (the doc tree, read by GitHub and editors) hold their trees to it, so
the platform's tolerance never turns a file another reader would drop into a green guard.

## Writing

`Serialize` writes block style with two-space indentation, a sequence under a key at the key's column
(`Tags:` then `- a`), and the most readable scalar form that reads back as the same value:

- **plain** when the text reads back as the same string — not when it would resolve to null, a boolean
  or a number, including the YAML 1.1 forms (`yes`, `off`, `1_000`, `1:20`) a strict 1.1 reader would
  convert;
- **single-quoted** (`'Foo: Bar'`, `'it''s'`) when plain would not survive;
- **double-quoted** with escapes only for control characters;
- **a literal block** (`|`, `|-`) for a multi-line string, with an indentation digit when its first line
  starts with a space.

Every string written reads back identically through the tolerant AND the strict reader
(`TolerantYamlTest.Serialize_StringsRoundTripExactly`).

## How it was checked

Besides the unit tests (`test/MeshWeaver.Hosting.Test/TolerantYamlTest.cs`,
`MarkdownFrontMatterTolerantYamlTest.cs`), the reader was run differentially against the previous
third-party reader over every `.yml`/`.yaml` file and every front-matter block in this repository and
the plugins repository when it was introduced — 28,405 files. Every file the previous reader accepted
produced the same tree (scalar for scalar), every file re-serialized and re-read under strict mode came
back equal, and the 70 files the previous reader refused were all the `: `-inside-a-value shape the
tolerant reader now reads.

## Related

- [Declarative export and import](/Doc/Architecture/DeclarativeImportExport) — what the markdown parsers bind
- [Authoring Documentation](/Doc/Architecture/AuthoringDocumentation) — the front matter a doc page carries
