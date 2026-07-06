// Executes javascript & typescript Code-node snippets through the node kernel's execution core
// (the same `executeCode` the CodeWorker runs on every SubmitCodeRequest). Runs against the built
// output — `npm test` builds first. Node's built-in test runner, no extra deps.
import { test } from "node:test";
import assert from "node:assert/strict";
import { executeCode } from "../dist/worker.js";

test("javascript node: captures console output and the trailing-expression value", async () => {
  const r = await executeCode("console.log('hello node'); 6 * 7", "javascript");
  assert.equal(r.error, null);
  assert.equal(r.returnValue, 42);              // trailing bare expression = return value (REPL)
  assert.match(r.stdout, /hello node/);         // console.log captured
});

test("typescript node: types are stripped, then executed", async () => {
  const r = await executeCode("const answer: number = 6 * 7; console.log(`ts=${answer}`); answer", "typescript");
  assert.equal(r.error, null);
  assert.equal(r.returnValue, 42);
  assert.match(r.stdout, /ts=42/);
});

test("Inputs global carries caller parameters (as the C# kernel binds Inputs)", async () => {
  const r = await executeCode("Inputs.a + Inputs.b", "javascript", { a: 20, b: 22 });
  assert.equal(r.returnValue, 42);
});

test("a throwing snippet is captured as an error — the worker never wedges", async () => {
  const r = await executeCode("throw new Error('boom')", "javascript");
  assert.equal(r.returnValue, null);
  assert.match(r.error ?? "", /boom/);
});

test("an object return value round-trips JSON-able", async () => {
  const r = await executeCode("({ mean: 42, ok: true })", "javascript");
  assert.deepEqual(r.returnValue, { mean: 42, ok: true });
});
