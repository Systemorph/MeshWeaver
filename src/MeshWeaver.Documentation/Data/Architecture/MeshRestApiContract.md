---
Name: The /api/mesh REST Contract
Category: Architecture
Description: "What an HTTP caller of POST /api/mesh/* may rely on: a 200 carries the verb's JSON document and nothing else; a sentinel answer (Error:, Not found:, Unavailable:) is a non-2xx with a JSON envelope naming the sentence and its kind. Why the raw-string 200 was a contract defect (Plugins#1699) and where the one mapping lives."
---

# The `/api/mesh` REST Contract

`POST /api/mesh/*` mirrors the MCP tools one-to-one: every verb runs one `MeshOperations` method
and ships its result. Two hosts carry the mirror — the portal (`Memex.Portal.Shared`'s
`MeshApiEndpoints`) and the local sidecar (`Memex.LocalMesh`'s `LocalMeshApiEndpoints` in
MeshWeaver.Plugins) — and both are consumed by code that is not a language model: the grpc-web
SDK, portal-next's server-side render, the `memex` CLI, and every e2e that reads a node back over
HTTP. This page is the contract those callers may rely on.

## The contract

| the verb produced | status | body |
|---|---|---|
| a JSON document | **200** `application/json` | the document, verbatim |
| `Not found: …` | **404** | `{ "error": "Not found: …", "kind": "NotFound" }` |
| `Unavailable: …` | **503** | `{ "error": "Unavailable: …", "kind": "Unavailable" }` |
| `Error: …` | **500** | `{ "error": "Error: …", "kind": "Error" }` |

So `response.ok` means exactly one thing — *here is the document* — and a caller that checks the
status before parsing is correct by construction. The sentence is kept verbatim in `error` because
it is the part that names the path and the cause; `kind` is the machine-readable half.

Two things are deliberately NOT part of this contract:

- **The MCP tool result is untouched.** On `/mcp` the sentinel sentence IS the answer: the reader is
  a model, and the `Unavailable:` sentence in particular is written for it ("this is NOT 'not found':
  do not create, delete or recreate anything on the strength of it"). The REST mirror changes the
  *envelope*, never the sentence.
- **Statuses that are not verb answers keep their own meaning.** An unmapped `/api/mesh/…` route is a
  404 with `{ "error": "No API endpoint at …" }` and no `kind`; an upload refusal is a 400; a
  render-area timeout is a 504. A caller that wants to know whether a non-2xx is a verb answer reads
  `kind`.

## Why the raw string was a defect, not a style

`MeshOperations` verbs return a `string` that is either a JSON document or one of three prose
sentinels, and until 2026-09-13 both hosts shipped that string as `200 application/json`. The
comment beside it said the sentinel was *"just a JSON-quoted value the client can branch on"*. It
was never quoted: a bare `Unavailable: Doc/Guide — this read reached no verdict …` is not JSON.

The measured failure (MeshWeaver.Plugins#1699, Education's install e2e, 2026-09-11): the harness
posted `/api/mesh/get`, asserted `response.ok()` — which passed — and then died in `JSON.parse`
with `Unexpected token 'U', "Unavailabl"... is not valid JSON`, naming neither the path nor the
reason. Every programmatic consumer had to parse-and-catch to tell "here is the node" from "I could
not answer", and the ones that did not were failing with the least useful message available.

The fix is a status code, because that is the one signal every HTTP client already reads first.
Measured across the consumers before changing it: the grpc-web SDK (`rest.ts`, `mesh.ts`),
portal-next's SSR (`snapshot.ts`) and the CLI all check the status **before** sniffing the prefix,
so a non-2xx lands each of them on the branch it already had for the sentinel — and none of them
handled `Unavailable:` at all, so for that sentinel the old contract could only ever produce the
parse error.

## Where the one definition lives

`MeshWeaver.AI.OperationSentinel` (in `MeshWeaver.Mesh.Operations`, beside the verbs that write the
sentinels) owns the three prefixes and the classification `Classify(string) → SentinelVerdict?`
carrying the kind AND the HTTP status. Both hosts map through it, so the portal and the sidecar
cannot disagree, and the prefix test is exact rather than heuristic: a JSON document begins with
`{`, `[`, a quote, a digit or a keyword, never with `Not found:`.

The CLI is the one consumer that does not link it — `memex` is deliberately dependency-free — so it
recognises the envelope by its wire shape (`error` beside a `kind` naming one of the three) and
unwraps it back to the sentence. Its own contract is therefore unchanged: the sentence verbatim on
stdout, `Error:` to stderr with exit 1, any other non-2xx a `MemexCliException`.

## What pins it

`Memex.Portal.Shared.Test/MeshApiSentinelStatusTest` executes `MeshApiEndpoints.Ship` through a real
`HttpContext` and asserts the status and the bytes for all four rows — with the negative control
that a document whose *content* mentions the words still goes out verbatim with a 200 — plus the
CLI unwrap in both directions. With the classifier disabled, the three sentinel rows and the
classifier case go red; the document row stays green (the mapping never touches it).

## Related

- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — where `Not found:` and `Unavailable:`
  come from: a definitive absence versus a read that reached no verdict (#974)
- [MCP Authentication](/Doc/AI/McpAuthentication) — the credential contract of the same routes
