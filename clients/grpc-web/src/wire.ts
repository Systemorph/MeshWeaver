// THE MESH WIRE SHAPES — the one place either TypeScript SDK names a server field.
//
// Every message a client posts is a C# record on the other side, and the hub matches JSON members
// to record members case-insensitively — so `sourcePath` reaches `SourcePath`, but `source` reaches
// NOTHING: System.Text.Json drops an unmatched member silently, the record is constructed with the
// parameter at its default, and the operation fails with no error anywhere. That is exactly how
// `MoveNodeRequest { source, target }` shipped in both SDKs (issue #1475) and how `execute()` wrote
// `requestedStatus` at the NODE level, where MeshNode has no such member (the field lives on the
// ActivityLog CONTENT). Nothing throws, nothing logs; the write is simply gone.
//
// Two guards keep that from happening again, both in wireContract.test.ts:
//   1. Every field name below is checked against the C# record it mirrors, parsed out of the
//      contract sources in src/ — a rename on the server turns the client red.
//   2. This file is mirrored BYTE-IDENTICALLY into clients/typescript/src/wire.ts (the same
//      discipline clients/react/src/i18n mirrors the server string catalog with), and the test
//      asserts the two copies are equal — so a fix can never land in one SDK only, which is the
//      other half of how #1475 survived.
// It is therefore deliberately import-free and pure: the two copies must be byte-for-byte equal.

/** A message ready to post: the hub message type name plus its JSON body. */
export interface WireMessage {
  readonly type: string;
  readonly message: Record<string, unknown>;
}

/**
 * `MeshNodeReference(string? Path)` — the POLYMORPHIC WorkspaceReference selecting one node's
 * stream. The `$type` discriminator is required: without it the polymorphic converter cannot
 * resolve the reference and the subscribe/patch resolves against nothing.
 */
export function meshNodeReference(path: string): Record<string, unknown> {
  return { $type: "MeshNodeReference", path };
}

/** `CreateNodeRequest(MeshNode Node)` — node lifecycle, targeted at the owner partition's hub. */
export function createNodeRequest(node: Record<string, unknown>): WireMessage {
  return { type: "CreateNodeRequest", message: { node } };
}

/** `DeleteNodeRequest(string Path)` — routed to the node's own hub. */
export function deleteNodeRequest(path: string): WireMessage {
  return { type: "DeleteNodeRequest", message: { path } };
}

/** `MoveNodeRequest(string SourcePath, string TargetPath)` — routed to the SOURCE node's hub. */
export function moveNodeRequest(sourcePath: string, targetPath: string): WireMessage {
  return { type: "MoveNodeRequest", message: { sourcePath, targetPath } };
}

/** `CopyNodeRequest(string SourcePath, string TargetPath)` — routed to the SOURCE node's hub. */
export function copyNodeRequest(sourcePath: string, targetPath: string): WireMessage {
  return { type: "CopyNodeRequest", message: { sourcePath, targetPath } };
}

/**
 * `PatchDataRequest(WorkspaceReference Reference, RawJson Patch)` — the canonical mutation, an
 * RFC 7396 JSON-merge patch applied by the owning hub (the wire form of
 * `GetMeshNodeStream(path).Update(...)`). `patch` is raw JSON, merged against the MeshNode, so its
 * members are MeshNode members — node content goes under `content`.
 */
export function patchDataRequest(path: string, patch: Record<string, unknown>): WireMessage {
  return { type: "PatchDataRequest", message: { reference: meshNodeReference(path), patch } };
}

/** `SubscribeRequest(string StreamId, WorkspaceReference Reference)` — opens a node's live stream. */
export function subscribeRequest(path: string, streamId: string): WireMessage {
  return { type: "SubscribeRequest", message: { streamId, reference: meshNodeReference(path) } };
}

/**
 * The merge patch that requests an activity state transition — `ActivityLog.RequestedStatus`, which
 * lives on the node's CONTENT (the owning hub's WatchControlPlane subscription reacts to the flip).
 * MeshNode itself has no RequestedStatus, so a top-level `{ requestedStatus }` is dropped silently.
 */
export function activityStatusPatch(status: string): Record<string, unknown> {
  return { content: { requestedStatus: status } };
}
