---
Name: Tolerant YAML
Category: Architecture
Description: "The platform reads YAML with its own reader — YAML syntax, JSON semantics: the text becomes a JSON tree and binds under the same tolerant rules as the platform's JSON node files, a colon-space inside a value is text, and text with no single reading fails with one exception type that names the line and column."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M8 12h12"/><path d="M8 18h12"/><path d="M4 12h.01"/><path d="M4 18h.01"/></svg>
---

# Tolerant YAML

Front matter on agents, skills, slides and doc pages, GitHub workflow files, helm output: the platform reads all of
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
| `TolerantYaml.ScalarText(node)` | a scalar's text as the core schema resolved it (`"true"`, `"1.10"`, the string; `007` reads `"7"`) |
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

A tag written before a line break applies to the node on the next line before it is resolved
(`name: !!str` then `007` on the next line is the string `007`). In a block scalar, whitespace past the
content indentation is content (`|-` over `x` and a line of three spaces is `"x\n "`).

Covered syntax: block mappings and sequences (indented or not under a key, the compact `- key: v` form,
nested `- - a`), flow collections over several lines with trailing commas, plain scalars folded over
several lines, single-quoted (`''`) and double-quoted scalars with every YAML escape (`\n`, `\t`, `\xXX`,
`\uXXXX`, `\UXXXXXXXX`, an escaped line break), literal `|` and folded `>` block scalars with the `-`/`+`
chomping indicators and an explicit indentation digit (chomping keeps only the line breaks that were actually there, so a
block at the end of the text without a final break gains none), full-line and trailing `#` comments, `---` and
`...` document markers, `%YAML` and `%TAG` directives (a `%TAG` handle decides what a tag means),
verbatim tags (`!<tag:yaml.org,2002:str>`), explicit `? key` / `: value` entries in block and flow
collections, properties, aliases and one-line flow collections as implicit keys (`&k key: v`,
`*a : v`, `[a, b]: v` — a key that is not a scalar becomes its JSON text), anchors `&a` and aliases `*a`
(an alias is a deep copy), and merge keys `<<: *a` and `<<: [*a, *b]` (explicit keys win, then the
first source that names a key; every source must be a mapping). A CRLF line ending and a leading
byte-order mark are normalised away.

**Tags.** A core tag — `!!str`, `!!int`, `!!float`, `!!bool`, `!!null`, `!!map`, `!!seq`, spelled
`!!x`, verbatim or through a `%TAG` handle — resolves the node AS that type, and text of another type
is refused with the tag's position (`!!int "12"` is 12, `!!int true` is an error). The non-specific `!`
makes a plain scalar a string. An application tag (`!Ref`) carries nothing a JSON tree can use: the
node resolves exactly as it would untagged (`!Ref 123` is the number 123).

## Limits — hostile input fails fast, with a position

The reader parses files that arrive by import and sync, so input it cannot bound is refused as a
`YamlParseException`, never a hang or a crash:

- **Nesting deeper than 64 levels** (flow or block, properties-only lines included). The reader
  recurses per level; without the limit, deep nesting is an uncatchable stack overflow. 64 is also
  System.Text.Json's default `MaxDepth`, so nothing deeper could be bound anyway.
- **Alias and merge-key expansion beyond 100,000 copied nodes** per text — every node AND every null
  slot a copy allocates. Every alias is a deep copy, so anchors that alias earlier anchors grow
  exponentially ("billion laughs").
- **A NUL character** anywhere in the text (YAML does not allow one in a stream).
- **An escape that is not a Unicode scalar value** (`\U00110000`, a lone `\uD800`). A `\u` high
  surrogate followed by a `\u` low surrogate, the JSON spelling of one character, is read as that pair.

Every loop in the reader consumes the text it inspects (a whitespace run inside a quoted scalar is
read once, not re-scanned from each of its characters), and a duplicate key is replaced in place, so
reading time is linear in the text. `TolerantYamlTest.GeneratedHostileInputs_…` throws several hundred
generated hostile texts at both modes — long whitespace runs in every scalar style, wide and deep
collections, alias and merge fans, million-character lines, tabs, indicator soup, one-character mutants
of a valid document — and requires each to parse or refuse with `YamlParseException` within its own
time budget.

## The tolerances

**In the reader.** Two places where malformed YAML has exactly one sensible reading:

- **A `: ` inside a plain value is text.** `description: new deal: fund pilot` reads as the string
  `new deal: fund pilot`. A mapping cannot start in the middle of a value line, so the only other
  reading is a refusal — and the refusal is what used to lose the whole block.
- **A duplicate key keeps its last value**, as System.Text.Json does with a duplicate JSON property.

Everything else that has no single reading — an unterminated quote or flow collection, an EMPTY flow
entry (`[a,,b]`, `{,}`; a trailing comma, an empty value `{k: }` and an empty key `{: v}` are legal),
an unknown alias, a tab in indentation, an entry indented between two levels, text after a closing
quote — throws `YamlParseException`.

**In the binding** (`CreateSerializerOptions`, the default for `Deserialize<T>`):

| Rule | So that |
|---|---|
| property names case-insensitive, written camelCase | `nodeType:`, `NodeType:` and `NODETYPE:` all bind |
| unknown keys skipped | an author's own metadata never fails a read |
| numbers readable from strings, named float literals | `order: "7"` binds to an `int` |
| any scalar binds to a `string` as its written text | `version: 1.10` gives `"1.10"`, `name: 123` gives `"123"`, `zip: 00123` gives `"00123"`, `label: TRUE` gives `"TRUE"` |
| a `"true"`/`"false"` string binds to a `bool` | quoting a switch does not break it |
| enums by name, case-insensitively | `colour: green` binds to `Colour.Green` |
| nulls omitted on write | a written file carries only what is set |
| a missing key keeps the property's default | as in JSON |

**How a string target gets the written text.** The tree stays YAML 1.2 core schema — `00123` is the
number 123, `0x1F` is 31, `TRUE` is `true` — because other readers consume it as JSON. While parsing,
the reader records, for THAT parse only, the source spelling of every plain scalar whose resolved node
reads differently, keyed by the node's reference (an alias or a merged key copies the spelling with the
node). `Deserialize<T>` then walks `T`'s System.Text.Json contract alongside the tree — properties
(matched like the binding, case-insensitively), collection elements, dictionary values, at any depth —
and wherever a `string` is declared puts the recorded spelling back before binding. A typed target
(`int`, `bool`, `double`, `object`, `JsonElement`) binds the resolved value, a property with its own
converter is left alone, and options without the tolerant string converter keep System.Text.Json's
refusal of a number for a string (#7398). Before this, a string target got the RESOLVED text (`"123"`,
`"31"`, `"true"`), which the previous third-party reader never did. `Parse` + `ScalarText` still answer
with the resolved text; a plain mapping KEY was always its written text (`007: x` has the key `007`).

A caller may pass its own `JsonSerializerOptions`; `MarkdownFileParser` writes front matter with
PascalCase keys in declaration order, which keeps every existing mirrored file byte-identical.

## Strict mode — for files strict readers also read

`YamlParseOptions.StrictSpec` refuses what the grammar refuses where the tolerant reader reads on: the
two reader tolerances above, a plain scalar (or plain key) starting with `@`, a backtick, `%`, `,`, `]`
or `}`, a lone `-` in a flow collection, a leading empty line of a block scalar indented past its
content, a `#` comment not separated from the content by a space, a block sequence on the line of its
key or properties, two anchors or two tags on one node, properties on an alias, an implicit flow key
spanning lines, continuation lines of a flow collection or quoted scalar not indented past their
block, and invalid directives (a directive without a `---` document, a malformed or repeated `%YAML`,
a tag handle no `%TAG` declares). The test-suite section below lists every case where the tolerant
reader accepts what strict mode refuses. The platform's own reads are tolerant; strict mode is for guards over files that OTHER readers
also consume — `SkillFrontMatterGuard` (`.claude/skills`, read by the coding harnesses) and
`DocumentationFrontMatterGuard` (the doc tree, read by GitHub and editors) hold their trees to it, so
the platform's tolerance never turns a file another reader would drop into a green guard.

## Conformance — the official YAML test suite

The reader is checked against the official YAML test suite (`yaml/yaml-test-suite`, release
`data-2022-01-17`, MIT licence), vendored under `test/MeshWeaver.Hosting.Test/YamlTestSuite/` and run by
`YamlTestSuiteConformanceTest` in both modes. Of its 402 cases, strict mode produces exactly the
expected JSON for **274**, refuses all **85** error cases it is expected to refuse, and parses the **27**
valid cases that have no JSON form (complex keys) without an error — **386 of 402**. The remaining
**16** are the reasoned exceptions below. The test is a two-way ratchet: a listed case that starts
passing fails the test until its line is removed, so a reviewer finding can always be answered against
the suite.

**Known unsupported (strict mode):**

- **Tabs** — `6CA3`, `DK95/00`, `DK95/01`, `Q5MG`, `Y79Y/000`, `Y79Y/003`–`Y79Y/009`. The reader refuses
  any tab in the leading whitespace of a content line and accepts tabs between tokens; YAML 1.2 allows a
  tab after the indentation in some positions and forbids it in others, and that finer rule is not
  implemented.
- **End of input without a final line break** — `JEF9/02`, `L24T/01`. The suite's reference output
  treats the end of the text as a line break that clip and keep chomping keep; this reader, like libyaml
  (Ruby's Psych) and YamlDotNet, adds no break that is not in the text.
- **Duplicate keys** — `2JQS`, `X38W`. Strict mode refuses a repeated key, because a JSON object cannot
  hold two; the suite treats key uniqueness as a check above the syntax.

**Error cases the tolerant reader deliberately accepts (44):**

| Leniency | Cases |
|---|---|
| a `: ` inside a plain value is text | `2CMS`, `9KBC`, `CXX2`, `EW3V`, `HU3P`, `ZCZ6` |
| a block sequence on the line of its key or properties | `5U3A`, `SY6V` |
| repeated properties (the last wins), properties on an alias | `4JVG`, `SR86`, `SU74` |
| an unknown escape is kept verbatim | `55WF`, `HRE5` |
| a leading block-scalar line indented past the content is empty | `5LLU`, `S98Z`, `W9L4` |
| continuation lines need no extra indentation | `9C9N`, `QB6E`, `VJP3/00` |
| `#` straight after a token is a comment | `9JBA`, `CVW2`, `SU5Z`, `X4QW` |
| directives are not validated | `9MMA`, `B63P`, `H7TQ`, `MUS6/00`, `SF5V`, `QLJ7` |
| a plain scalar may start with `%` or `,` | `MUS6/01`, `U99R` |
| an implicit flow key may span lines | `DK4H`, `ZXT5` |
| a lone `-` in a flow collection is the string `-` | `G5U8`, `YJV2` |
| tabs (see above) | `DK95/01`, `Y79Y/000`, `Y79Y/003`–`Y79Y/009` |

On every VALID case the tolerant reader gives the same answer as strict mode.

## Writing

`Serialize` writes block style with two-space indentation, a sequence under a key at the key's column
(`Tags:` then `- a`), and the most readable scalar form that reads back as the same value:

- **plain** when the text reads back as the same string — not when it would resolve to null, a boolean,
  a number or a time, including the YAML 1.1 forms a 1.1 reader would convert (`yes`, `off`, `y`,
  `0xFF_FF`, `-0xFF`, `0b101`, `017`, `1_000`, `1,000`, `1:20`, `1.2.3`, `.Inf`, anything starting
  like a date such as `2026-01-01`) — checked against an independent YAML 1.1 reader (Ruby's Psych);
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

## No third-party YAML library in the platform

The platform image carries no other YAML library. The modules that used one — MeshWeaver.Plugins'
`MeshWeaver.AI` (agent and skill front matter) and `MeshWeaver.Publish` (slide front matter) — read
and write through `TolerantYaml` as well (MeshWeaver.Plugins#4227), so YamlDotNet left the platform's
package list. Before that port it was checked differentially over every front-matter block in this
repository, the plugins repository and every satellite (7,872 blocks): every block the previous
reader accepted bound to identical objects, the 13 it refused were the `: `-inside-a-value shape, and
the writers differ only in writing non-BMP characters literally and single-quoting where the previous
writer double-quoted.

A module bundle built while it still referenced YamlDotNet ships its own copy in its closure. On an
image without YamlDotNet that copy is outside the link gate's denominator (the platform does not carry
the name — [Module Platform Link Gate](../ModulePlatformLinkGate)), so the module links and loads its
own copy, as any private dependency does.

## Related

- [Declarative export and import](/Doc/Architecture/DeclarativeImportExport) — what the markdown parsers bind
- [Authoring Documentation](/Doc/Architecture/AuthoringDocumentation) — the front matter a doc page carries
