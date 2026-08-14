import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  activityStatusPatch,
  copyNodeRequest,
  createNodeRequest,
  deleteNodeRequest,
  meshNodeReference,
  moveNodeRequest,
  patchDataRequest,
  subscribeRequest,
  type WireMessage,
} from "./wire";

// THE DRIFT GUARD for src/wire.ts (issue #1475).
//
// Every message the SDKs post is a C# record on the server. The hub matches JSON members to record
// members case-insensitively, so a MISSPELLED member is not an error — System.Text.Json drops it,
// the record is built with that parameter at its default, and the operation fails with nothing
// logged anywhere. `MoveNodeRequest { source, target }` against `MoveNodeRequest(string SourcePath,
// string TargetPath)` shipped in BOTH SDKs for exactly that reason: no test looked at the payload.
//
// So this test reads the C# contract sources and checks each field name the client sends really is
// a member of the record it names. A server-side rename now turns the client red instead of turning
// a client feature silently into a no-op.
//
// 🚨 The files read below are cross-tree inputs: .github/workflows/clients.yml must be triggered by
// them, or a server-only rename would not run this job. clients/react/src/ciTrigger.test.ts is the
// guard that enforces that.

const pkgRoot = process.cwd();
const CONTRACT_SOURCES = [
  "../../src/MeshWeaver.Mesh.Contract/CreateNodeRequest.cs",
  "../../src/MeshWeaver.Mesh.Contract/MeshNode.cs",
  "../../src/MeshWeaver.Mesh.Contract/MeshNodeReference.cs",
  "../../src/MeshWeaver.Data.Contract/Messages.cs",
  "../../src/MeshWeaver.Data.Contract/PatchDataRequest.cs",
];

const contractSource = CONTRACT_SOURCES.map((p) => readFileSync(resolve(pkgRoot, p), "utf8")).join("\n");

/** Strip comments and string/char literals — they carry braces, commas and identifiers that would
 *  otherwise be parsed as declarations (`$"{Namespace}/{Id}"`, an XML doc `<see cref="X"/>`). */
function stripNoise(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    .replace(/\/\/[^\n]*/g, " ")
    .replace(/@"(?:[^"]|"")*"/g, '""')
    .replace(/"(?:\\.|[^"\\])*"/g, '""')
    .replace(/'(?:\\.|[^'\\])*'/g, "''");
}

const source = stripNoise(contractSource);

/** Split a parameter list on TOP-LEVEL commas (generic arguments and attributes carry their own). */
function splitTopLevel(list: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < list.length; i++) {
    const c = list[i];
    if (c === "(" || c === "[" || c === "<") depth++;
    else if (c === ")" || c === "]" || c === ">") depth--;
    else if (c === "," && depth === 0) {
      parts.push(list.slice(start, i));
      start = i + 1;
    }
  }
  parts.push(list.slice(start));
  return parts.filter((p) => p.trim().length > 0);
}

/**
 * The declared members of a C# record: its positional parameters plus the `{ get; … }` /
 * expression-bodied properties in its body. The body is taken as the text up to the next top-level
 * type declaration — enough for these flat contract records, and far less brittle than counting
 * braces through method bodies.
 */
function recordMembers(name: string): string[] {
  const decl = new RegExp(String.raw`\brecord\s+${name}\s*(\(|:|;|\{)`).exec(source);
  if (!decl) throw new Error(`no C# record '${name}' in ${CONTRACT_SOURCES.join(", ")}`);

  const members: string[] = [];
  let cursor = decl.index + decl[0].length - 1;
  if (source[cursor] === "(") {
    let depth = 0;
    let end = cursor;
    for (; end < source.length; end++) {
      if (source[end] === "(") depth++;
      else if (source[end] === ")" && --depth === 0) break;
    }
    for (const param of splitTopLevel(source.slice(cursor + 1, end))) {
      // "[property: Editable(false)] string? Namespace = null" → Namespace
      const bare = param.replace(/\[[^\]]*\]/g, " ").split("=")[0];
      const id = /(\w+)\s*$/.exec(bare.trim());
      if (id) members.push(id[1]);
    }
    cursor = end;
  }

  const rest = source.slice(cursor);
  const nextType = /\b(?:record|class|interface|enum)\s+\w+/;
  const boundary = nextType.exec(rest);
  const body = rest.slice(0, boundary?.index ?? rest.length);
  // "public IReadOnlyList<string>? Tags { get; init; }" / "public string Path => …" — a "(" before
  // the name means it is a method, so the character class excludes it.
  for (const m of body.matchAll(/\bpublic\s+[^;(){}]*?\b(\w+)\s*(?:\{\s*get|=>)/g)) members.push(m[1]);
  return members;
}

/** Assert every field of `message` is a declared member of the C# record `recordName`. */
function expectMembers(recordName: string, message: Record<string, unknown>): void {
  const declared = recordMembers(recordName).map((m) => m.toLowerCase());
  expect(declared.length, `parsed no members off record ${recordName} — the parser broke`).toBeGreaterThan(0);
  const unknown = Object.keys(message)
    .filter((k) => k !== "$type") // the polymorphic discriminator, not a record member
    .filter((k) => !declared.includes(k.toLowerCase()));
  expect(
    unknown,
    `${recordName} has no member(s) ${unknown.join(", ")} — the hub would drop them SILENTLY. ` +
      `Declared: ${declared.join(", ")}`,
  ).toEqual([]);
}

const wireOf = (m: WireMessage) => m.message;

describe("every field the SDK sends exists on the C# record it names", () => {
  it("parses the contract sources (a broken parser must not pass vacuously)", () => {
    expect(recordMembers("MoveNodeRequest")).toEqual(expect.arrayContaining(["SourcePath", "TargetPath"]));
    expect(recordMembers("MeshNode")).toEqual(expect.arrayContaining(["Id", "Namespace", "Content", "NodeType"]));
    expect(recordMembers("PatchDataRequest")).toEqual(expect.arrayContaining(["Reference", "Patch"]));
  });

  it("CreateNodeRequest", () => expectMembers("CreateNodeRequest", wireOf(createNodeRequest({ id: "x" }))));
  it("DeleteNodeRequest", () => expectMembers("DeleteNodeRequest", wireOf(deleteNodeRequest("ACME/Old"))));
  it("MoveNodeRequest", () => expectMembers("MoveNodeRequest", wireOf(moveNodeRequest("ACME/A", "ACME/B"))));
  it("CopyNodeRequest", () => expectMembers("CopyNodeRequest", wireOf(copyNodeRequest("ACME/A", "ACME/C"))));
  it("PatchDataRequest", () => expectMembers("PatchDataRequest", wireOf(patchDataRequest("ACME/A", { name: "n" }))));
  it("SubscribeRequest", () => expectMembers("SubscribeRequest", wireOf(subscribeRequest("ACME/A", "s1"))));
  it("MeshNodeReference", () => expectMembers("MeshNodeReference", meshNodeReference("ACME/A")));

  it("keeps the reference's polymorphic discriminator (without it nothing resolves)", () => {
    expect(meshNodeReference("ACME/A").$type).toBe("MeshNodeReference");
    expect((wireOf(subscribeRequest("ACME/A", "s1")).reference as Record<string, unknown>).$type).toBe(
      "MeshNodeReference",
    );
  });

  // A merge patch is applied to the MeshNode, so its top-level members are MeshNode's. This is the
  // check that catches `execute()`'s old top-level `{ requestedStatus }`: RequestedStatus lives on
  // the ActivityLog CONTENT, and MeshNode has no such member — so the flip never reached the hub.
  it("a merge patch names MeshNode members", () => {
    expectMembers("MeshNode", wireOf(patchDataRequest("p", { name: "n", content: { anything: 1 } })).patch as Record<string, unknown>);
    expectMembers("MeshNode", activityStatusPatch("Running"));
  });

  it("the activity trigger sits on the node's content, not the node", () => {
    expect(activityStatusPatch("Running")).toEqual({ content: { requestedStatus: "Running" } });
  });
});

const WEB_WIRE = "src/wire.ts";
const NODE_WIRE = "../typescript/src/wire.ts";

// Every file the two SDKs must carry ONE copy of. `wire.ts` is the original (#1475); `jsonPatch.ts`
// and `changeFold.ts` joined it for #1496, where the Node SDK's own decode treated the
// DataChangedEvent AS the node and no test compared it against the browser twin that had it right.
const MIRRORED = [
  ["src/wire.ts", "../typescript/src/wire.ts"],
  ["src/jsonPatch.ts", "../typescript/src/jsonPatch.ts"],
  ["src/changeFold.ts", "../typescript/src/changeFold.ts"],
] as const;

describe("the two TypeScript SDKs share ONE copy of every mirrored module", () => {
  for (const [web, node] of MIRRORED) {
    it(`${node} is byte-identical to ${web}`, () => {
      const a: string[] = readFileSync(resolve(pkgRoot, web), "utf8").split("\n");
      const b: string[] = readFileSync(resolve(pkgRoot, node), "utf8").split("\n");
      const at = a.findIndex((line: string, i: number) => line !== b[i]);
      expect(
        at === -1 && a.length === b.length,
        at === -1
          ? `${node} has ${b.length} lines vs ${a.length} in ${web} — copy one over the other.`
          : `${node} diverged from ${web} at line ${at + 1}:\n` +
            `  grpc-web: ${a[at]}\n  node    : ${b[at] ?? "(missing)"}\n` +
            `The two SDKs must carry ONE copy — copy one file over the other.`,
      ).toBe(true);
    });
  }
});

describe("the two TypeScript SDKs share ONE copy of the wire shapes", () => {
  // #1475 shipped in both SDKs at once because each hand-mirrored the other. Byte-identical copies
  // (the discipline clients/react/src/i18n uses for the server string catalog) make a one-sided fix
  // impossible: wire.ts is import-free and pure precisely so the two files can be equal.
  it("clients/typescript/src/wire.ts is byte-identical to clients/grpc-web/src/wire.ts", () => {
    const web = readFileSync(resolve(pkgRoot, WEB_WIRE), "utf8").split("\n");
    const node = readFileSync(resolve(pkgRoot, NODE_WIRE), "utf8").split("\n");
    // Report the FIRST differing line rather than `expect(a).toBe(b)` on two 77-line strings, whose
    // failure output is two identical-looking truncated prefixes and tells a reviewer nothing.
    const at = web.findIndex((line, i) => line !== node[i]);
    expect(
      at === -1 && web.length === node.length,
      at === -1
        ? `${NODE_WIRE} has ${node.length} lines vs ${web.length} in ${WEB_WIRE} — copy one over the other.`
        : `${NODE_WIRE} diverged from ${WEB_WIRE} at line ${at + 1}:\n` +
          `  grpc-web: ${web[at]}\n  node    : ${node[at] ?? "(missing)"}\n` +
          `The two SDKs must carry ONE copy of the wire shapes — copy one file over the other.`,
    ).toBe(true);
  });

  // The byte-identity check only guards what is INSIDE wire.ts. A future edit that builds a message
  // inline in mesh.ts would bypass both guards, which is precisely the state #1475 shipped from. So:
  // every message type wire.ts owns must be named ONLY there. Derived from wire.ts itself, so adding
  // a builder extends the guard automatically — and a type wire.ts does NOT own (a REST-backed verb,
  // say) is correctly out of scope rather than hard-coded into an exception list.
  it("neither SDK names a wire.ts-owned message type inline", () => {
    const owned = [...readFileSync(resolve(pkgRoot, WEB_WIRE), "utf8").matchAll(/type:\s*"(\w+)"/g)].map((m) => m[1]);
    expect(owned.length, "scraped no message types off wire.ts — the scrape broke").toBeGreaterThan(3);

    for (const meshFile of ["src/mesh.ts", "../typescript/src/mesh.ts"]) {
      const source = readFileSync(resolve(pkgRoot, meshFile), "utf8");
      const inlined = owned.filter((type) => source.includes(`"${type}"`));
      expect(
        inlined,
        `${meshFile} names ${inlined.join(", ")} as a string literal. Those shapes belong to wire.ts, ` +
          `where the field names are checked against the C# records — building the message inline ` +
          `re-opens exactly the drift #1475 came from.`,
      ).toEqual([]);
    }
  });
});
