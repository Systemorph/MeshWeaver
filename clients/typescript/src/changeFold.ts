// THE CHANGE FOLD — how either TypeScript SDK turns a DataChangedEvent into the node's state.
//
// A subscription does NOT deliver nodes. It delivers `DataChangedEvent { changeType, change,
// streamId }`, where `change` is the payload and `changeType` says how to apply it:
//   * "Full"  — `change` IS the whole node JSON; it replaces the accumulated state.
//   * "Patch" — `change` is an RFC 6902 patch ARRAY applied at the NODE ROOT
//               (JsonSynchronizationStream.CreateSingleObjectPatch), folded onto the state so far.
//   * "NoUpdate" — nothing changed; emit nothing.
// `change` may arrive as a JSON STRING (RawJson on the wire) rather than an object.
//
// 🚨 Why this is a shared module and not two implementations. The Node SDK used to yield
// `meshNodeFromChange(delivery.message)` — i.e. it treated the EVENT as the node. `DataChangedEvent`
// has none of `path` / `name` / `nodeType` / `content`, so every emission was
// `{ path: undefined, name: undefined, nodeType: undefined, content: {} }` and every consequence was
// silent (issue #1496): `get()` resolved to "the node exists but is empty" rather than erroring, and
// `submitMessage` read `content["userMessageIds"]` off that value, saw `[]`, and sent a patch that
// REPLACED the thread's id list with a single id — dropping every earlier message under RFC 7396
// array semantics. The browser twin had the fold right the whole time; only the copy without a test
// was wrong.
//
// So, exactly like wire.ts: this file is import-free and pure, and it is mirrored BYTE-IDENTICALLY
// into clients/typescript/src/changeFold.ts. wireContract.test.ts asserts the two copies are equal,
// so a fix can never again land in one SDK only.

// The `.js` specifier is required by the Node SDK's NodeNext resolution and accepted by
// grpc-web's Bundler resolution — the one spelling under which this file can be byte-identical
// in both packages.
import { applyJsonPatch, type PatchOperation } from "./jsonPatch.js";

/** The accumulated node JSON a fold carries between emissions. */
export type NodeState = Record<string, unknown>;

/**
 * Fold one delivery message onto the state so far.
 *
 * @param message  the DataChangedEvent as received (camelCase or PascalCase members).
 * @param previous the node state accumulated from earlier deliveries, or null at stream start.
 * @returns the new node state, or null when the delivery carries no update (emit nothing).
 */
export function foldChange(message: Record<string, unknown>, previous: NodeState | null): NodeState | null {
  // 🚨 ABSENT is not NULL. Probing with `message["change"] ?? message["Change"]` and testing the
  // result for `undefined` collapses the two: a DataChangedEvent carrying an explicit null Change
  // reads as "no change member at all" and the whole EVENT is then returned as the node — the same
  // class of decode error this module exists to end. The membership test keeps them apart.
  const hasChange = "change" in message || "Change" in message;

  // No `change` member at all: the node's fields are flat on the message itself. That is what
  // in-memory fakes and the legacy shapes produce, and it stays supported so a test transport does
  // not have to model the event envelope.
  if (!hasChange) return message as NodeState;

  const rawChange = message["change"] ?? message["Change"];
  const change = typeof rawChange === "string" ? (JSON.parse(rawChange) as unknown) : rawChange;
  const changeType = String(message["changeType"] ?? message["ChangeType"] ?? "Full");
  if (change == null || changeType === "NoUpdate") return null;

  // "1" is ChangeType.Patch's ordinal — a serializer that writes the enum numerically still folds.
  return changeType === "Patch" || changeType === "1"
    ? (applyJsonPatch(previous ?? {}, change as PatchOperation[]) as NodeState)
    : (change as NodeState);
}
