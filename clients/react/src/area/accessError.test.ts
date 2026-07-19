import { describe, it, expect } from "vitest";
import {
  classifyAreaError,
  getAccessDeniedPath,
  isAccessDenied,
  isNodeGone,
  isSafeRedirect,
} from "./accessError.js";

// Mirrors test/MeshWeaver.Layout.Test/AreaErrorClassifierTest.cs — the TS classifier must make the
// SAME decisions the server AreaErrorClassifier makes, so the shells' redirect/denied behavior stays
// 1:1 with the Blazor NamedAreaView.

describe("getAccessDeniedPath", () => {
  it.each([
    ["Access denied: user 'roland.buergi' lacks Read permission on 'AgenticEngineering'", "AgenticEngineering"],
    ["Access denied: user 'u' lacks Read permission on 'AgenticEngineering/Module1'", "AgenticEngineering/Module1"],
    ["User 'u' lacks Read permission on 'DataModeling'", "DataModeling"],
  ])("extracts the path from %s", (message, expected) => {
    expect(getAccessDeniedPath(message)).toBe(expected);
  });

  it.each([
    ["No node found at 'x/y'."], // routing NotFound — different banner
    ["Validation failed for field 'name'"], // no "permission on '"
    ["boom"],
  ])("is null when there is no quoted permission path: %s", (message) => {
    expect(getAccessDeniedPath(message)).toBeNull();
  });

  it("is null for null/empty", () => {
    expect(getAccessDeniedPath(null)).toBeNull();
    expect(getAccessDeniedPath(undefined)).toBeNull();
    expect(getAccessDeniedPath("")).toBeNull();
  });
});

describe("isAccessDenied", () => {
  it.each([
    "Access denied: user lacks Read",
    "User 'u' lacks Read permission on 'X'",
    "Unauthorized",
    "Forbidden",
  ])("is true for a denial: %s", (message) => {
    expect(isAccessDenied(message)).toBe(true);
  });

  it.each(["No node found at 'x/y'.", "Validation failed for X", "boom", ""])(
    "is false for non-denials: %s",
    (message) => {
      expect(isAccessDenied(message)).toBe(false);
    },
  );
});

describe("isNodeGone", () => {
  it.each([
    "No node found at Foo/Bar",
    "No node found at 'rbuergi/_Activity/markdown-abc'. Closest ancestor is 'rbuergi' (remainder='_Activity/markdown-abc').",
  ])("is true for routing NotFound: %s", (message) => {
    expect(isNodeGone(message)).toBe(true);
  });

  it.each(["Access denied: user lacks Read", "Validation failed for X", ""])(
    "is false for other failures: %s",
    (message) => {
      expect(isNodeGone(message)).toBe(false);
    },
  );
});

describe("isSafeRedirect (loop-guard)", () => {
  it.each<[string | null, string | null, boolean]>([
    // A gated node redirects to a DIFFERENT public page → safe.
    ["AgenticEngineering/Introduction", "AgenticEngineering/Cover", true],
    // A leading '/' on the target is ignored.
    ["AgenticEngineering/Introduction", "/AgenticEngineering/Cover", true],
    // The redirect TARGET itself being denied → would loop → NOT safe.
    ["AgenticEngineering/Cover", "AgenticEngineering/Cover", false],
    // A node UNDER the target being denied → would loop → NOT safe.
    ["AgenticEngineering/Cover/Sub", "AgenticEngineering/Cover", false],
    // No target configured → nothing to redirect to.
    ["AgenticEngineering/Introduction", null, false],
    ["AgenticEngineering/Introduction", "", false],
    ["AgenticEngineering/Introduction", "   ", false],
    // No denied path → nothing to redirect.
    [null, "AgenticEngineering/Cover", false],
  ])("isSafeRedirect(%s, %s) === %s", (deniedPath, redirectPath, expected) => {
    expect(isSafeRedirect(deniedPath, redirectPath)).toBe(expected);
  });
});

describe("classifyAreaError", () => {
  it("classifies an access denial with its path", () => {
    expect(classifyAreaError("Access denied: user 'u' lacks Read permission on 'Course/Lesson'")).toEqual({
      message: "Access denied: user 'u' lacks Read permission on 'Course/Lesson'",
      kind: "access-denied",
      deniedPath: "Course/Lesson",
    });
  });

  it("classifies a routing NotFound as not-found with no denied path", () => {
    const info = classifyAreaError("No node found at 'x/y'.");
    expect(info?.kind).toBe("not-found");
    expect(info?.deniedPath).toBeNull();
  });

  it("classifies an engineering fault as other", () => {
    expect(classifyAreaError("boom")?.kind).toBe("other");
  });

  it("is null for a null/empty message", () => {
    expect(classifyAreaError(null)).toBeNull();
    expect(classifyAreaError("")).toBeNull();
  });
});
