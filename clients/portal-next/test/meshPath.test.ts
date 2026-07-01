// URL ↔ mesh path mapping for the [[...meshPath]] route.
import { describe, expect, it } from "vitest";
import { hrefForMeshPath, meshPathFromSegments } from "../src/meshPath";

describe("meshPathFromSegments", () => {
  it("maps no segments to the empty (home) path", () => {
    expect(meshPathFromSegments(undefined)).toBe("");
    expect(meshPathFromSegments(null)).toBe("");
    expect(meshPathFromSegments([])).toBe("");
  });

  it("joins segments into a mesh path", () => {
    expect(meshPathFromSegments(["Doc", "GUI"])).toBe("Doc/GUI");
    expect(meshPathFromSegments(["rbuergi"])).toBe("rbuergi");
  });

  it("decodes URL-encoded segments (Next delivers catch-all segments raw)", () => {
    expect(meshPathFromSegments(["My%20Space", "Sub%2FArea"])).toBe("My Space/Sub/Area");
  });

  it("tolerates malformed escapes instead of throwing", () => {
    expect(meshPathFromSegments(["100%"])).toBe("100%");
  });

  it("drops empty and traversal segments", () => {
    expect(meshPathFromSegments(["", "Doc", ".", "..", "GUI", ""])).toBe("Doc/GUI");
  });
});

describe("hrefForMeshPath", () => {
  it("maps home to /", () => {
    expect(hrefForMeshPath("")).toBe("/");
  });

  it("encodes each segment", () => {
    expect(hrefForMeshPath("Doc/GUI")).toBe("/Doc/GUI");
    expect(hrefForMeshPath("My Space/Notes")).toBe("/My%20Space/Notes");
  });
});
