---
Name: /api/mesh answers a degraded read with a status code, not prose in a 200
Category: Fix
Description: A POST /api/mesh/* verb that could not answer — a node that is not there, a read that reached no verdict, a fault — now responds with 404 / 503 / 500 and a JSON body { error, kind } instead of a 200 whose body was a sentence. response.ok means "here is the document" again.
Icon: ShieldCheck
Order: -20260913
---

# `/api/mesh` answers a degraded read with a status code, not prose in a 200

Every `POST /api/mesh/*` verb used to ship whatever string the operation produced with a **200** and
`application/json` — including the three prose sentinels (`Error: …`, `Not found: …`,
`Unavailable: …`), which are not JSON. A caller that checked `response.ok()` and then parsed the
body died inside `JSON.parse` with nothing naming the path or the reason.

Now a sentinel answer is a **404** (not found), **503** (the read reached no verdict — retry) or
**500** (a fault), with a JSON body `{ "error": "<the sentence>", "kind": "NotFound" | "Unavailable" | "Error" }`.
A document still goes out verbatim with a 200. The MCP tool result is unchanged, and the `memex`
CLI unwraps the envelope so its output and exit codes are what they were.

Contract and rationale: [The /api/mesh REST Contract](/Doc/Architecture/MeshRestApiContract).
