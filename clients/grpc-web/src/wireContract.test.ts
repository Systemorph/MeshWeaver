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

describe("the two TypeScript SDKs share ONE copy of the wire shapes", () => {
  // #1475 shipped in both SDKs at once because each hand-mirrored the other. Byte-identical copies
  // (the discipline clients/react/src/i18n uses for the server string catalog) make a one-sided fix
  // impossible: wire.ts is import-free and pure precisely so the two files can be equal.
  it("clients/typescript/src/wire.ts is byte-identical to clients/grpc-web/src/wire.ts", () => {
    const web = readFileSync(resolve(pkgRoot, "src/wire.ts"), "utf8");
    const node = readFileSync(resolve(pkgRoot, "../typescript/src/wire.ts"), "utf8");
    expect(node).toBe(web);
  });
});
