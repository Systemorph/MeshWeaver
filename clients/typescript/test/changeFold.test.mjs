// THE DECODE GUARD for this SDK (issue #1496) — the counterpart of
// clients/grpc-web/src/changeFold.test.ts, over the copy this package ships.
//
// `Mesh.watch` used to yield `meshNodeFromChange(delivery.message)`: it decoded the EVENT as the
// NODE. `DataChangedEvent` carries `{ changeType, change, streamId }` and none of the four keys
// `meshNodeFromChange` reads, so every emission was
// `{ path: undefined, name: undefined, nodeType: undefined, content: {} }` — and every consequence
// was silent. `get()` resolved to "the node exists but is empty" instead of erroring, and
// `submitMessage` read `content["userMessageIds"]` off that value, saw `[]`, and sent a patch that
// REPLACED the thread's id list with a single id, dropping every earlier message id under RFC 7396
// array semantics.
//
// It survived because this package had NO test for Mesh at all and its CI job ran `npm run
// typecheck` only — never `npm test`, though the script and the runner were both already here.
// That is fixed too: .github/workflows/clients.yml's node-sdk job now runs the tests.
//
// Runs against the built output — `npm test` builds first.
import { test } from "node:test";
import assert from "node:assert/strict";
import { foldChange } from "../dist/changeFold.js";

const node = (name) => ({
  path: "acme/Thing",
  name,
  nodeType: "Markdown",
  content: { title: name, userMessageIds: ["m1", "m2"] },
});

test("Full replaces the state with the node the change carries", () => {
  const out = foldChange({ changeType: "Full", change: node("first"), streamId: "s" }, null);

  assert.equal(out.path, "acme/Thing");
  assert.equal(out.name, "first");
  // The regression in one line: decoding the EVENT would leave content empty, which is what made
  // submitMessage drop the earlier message ids.
  assert.deepEqual(out.content.userMessageIds, ["m1", "m2"]);
});

test("a change delivered as a JSON string (RawJson on the wire) decodes", () => {
  const out = foldChange({ changeType: "Full", change: JSON.stringify(node("stringy")) }, null);
  assert.equal(out.name, "stringy");
});

test("PascalCase members decode identically", () => {
  const out = foldChange({ ChangeType: "Full", Change: node("pascal") }, null);
  assert.equal(out.name, "pascal");
});

test("Patch folds an RFC 6902 array onto the state so far, at the node root", () => {
  const first = foldChange({ changeType: "Full", change: node("first") }, null);
  const out = foldChange(
    { changeType: "Patch", change: [{ op: "replace", path: "/name", value: "second" }] },
    first,
  );

  assert.equal(out.name, "second");
  assert.equal(out.path, "acme/Thing", "a patch folds onto the node — it does not replace it");
});

test("NoUpdate and a null change emit nothing", () => {
  assert.equal(foldChange({ changeType: "NoUpdate", change: {} }, node("prev")), null);
  // ABSENT is not NULL: an explicit null Change must not be read as "no change member", which
  // would return the whole EVENT as the node.
  assert.equal(foldChange({ changeType: "Full", change: null }, null), null);
});

test("a message with no change member is taken as the node itself (test transports)", () => {
  const out = foldChange(node("flat"), null);
  assert.equal(out.name, "flat");
});
