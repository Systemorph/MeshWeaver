import { describe, expect, it } from "vitest";
import { foldChange } from "./changeFold";

// THE DECODE GUARD (issue #1496).
//
// A subscription delivers `DataChangedEvent { changeType, change, streamId }` — NOT a node. The
// Node SDK yielded `meshNodeFromChange(delivery.message)`, i.e. it decoded the EVENT as the NODE,
// and because `meshNodeFromChange` looks up four keys the event does not have, every emission was
// `{ path: undefined, name: undefined, nodeType: undefined, content: {} }`. Nothing threw:
//   * `get()` resolved to "the node exists but is empty" rather than erroring, and
//   * `submitMessage` read `content["userMessageIds"]` off that, saw `[]`, and sent a patch that
//     REPLACED the thread's id list with one id — dropping every earlier message (RFC 7396 arrays).
//
// It survived because `clients/typescript` had no test for `Mesh` at all and its CI job ran only
// `npm run typecheck`. Both halves are fixed: the fold is shared and mirrored, and the node-sdk job
// now runs `npm test`.

const node = (name: string, extra: Record<string, unknown> = {}) => ({
  path: "acme/Thing",
  name,
  nodeType: "Markdown",
  content: { title: name, userMessageIds: ["m1", "m2"] },
  ...extra,
});

describe("foldChange decodes the DataChangedEvent, not the event as the node", () => {
  it("Full replaces the state with the node the change carries", () => {
    const out = foldChange({ changeType: "Full", change: node("first"), streamId: "s" }, null);

    expect(out).not.toBeNull();
    expect(out!["path"]).toBe("acme/Thing");
    expect(out!["name"]).toBe("first");
    // The regression in one line: decoding the EVENT would leave content empty.
    expect((out!["content"] as Record<string, unknown>)["userMessageIds"]).toEqual(["m1", "m2"]);
  });

  it("accepts a change delivered as a JSON STRING (RawJson on the wire)", () => {
    const out = foldChange({ changeType: "Full", change: JSON.stringify(node("stringy")) }, null);

    expect(out!["name"]).toBe("stringy");
  });

  it("PascalCase members decode identically", () => {
    const out = foldChange({ ChangeType: "Full", Change: node("pascal") }, null);

    expect(out!["name"]).toBe("pascal");
  });

  it("Patch folds an RFC 6902 array onto the state so far, at the node root", () => {
    const first = foldChange({ changeType: "Full", change: node("first") }, null);
    const out = foldChange(
      { changeType: "Patch", change: [{ op: "replace", path: "/name", value: "second" }] },
      first,
    );

    expect(out!["name"]).toBe("second");
    // The rest of the node survives the patch — a fold, not a replacement.
    expect(out!["path"]).toBe("acme/Thing");
  });

  it("the numeric ChangeType ordinal for Patch folds too", () => {
    const first = foldChange({ changeType: "Full", change: node("first") }, null);
    const out = foldChange({ changeType: "1", change: [{ op: "replace", path: "/name", value: "n" }] }, first);

    expect(out!["name"]).toBe("n");
  });

  it("NoUpdate emits nothing", () => {
    expect(foldChange({ changeType: "NoUpdate", change: {} }, node("prev"))).toBeNull();
  });

  it("a null change emits nothing", () => {
    expect(foldChange({ changeType: "Full", change: null }, null)).toBeNull();
  });

  it("a message with no `change` member is taken as the node itself (test transports)", () => {
    const out = foldChange(node("flat"), null);

    expect(out!["name"]).toBe("flat");
  });
});
