// Pins the shell's C#-ported behaviors against the Blazor source of truth:
//   - search relevance scoring (MeshSearch.ComputeRelevanceScore tiers, normalized per term)
//   - path-proximity boost (PathProximity.ComputeBoost — MaxBoost/(1+segmentDistance))
//   - the URL remainder → (area, id) split (ParseSidePanelRemainder / SplitAreaRemainder)
//   - hierarchical menu flattening (PortalLayoutBase.FlattenMenuItems)
//   - avatar initials (UserProfile.GetInitials)

import { describe, expect, it } from "vitest";
import { proximityBoost, relevanceScore } from "../src/client/SearchBar";
import { splitRemainder } from "../src/server/snapshot";
import { flattenMenuItems, type MenuItemDef } from "../src/client/HeaderMenus";
import { initialsOf } from "../src/client/UserProfileMenu";

describe("relevanceScore (MeshSearch.ComputeRelevanceScore port)", () => {
  const row = { name: "Pricing Model", path: "ACME/Pricing/Model", nodeType: "Story" };

  it("scores name-prefix matches highest (100)", () => {
    expect(relevanceScore(row, "pricing", null)).toBe(100);
  });

  it("scores name-contains at 80", () => {
    expect(relevanceScore(row, "model", null)).toBe(80);
  });

  it("scores path-contains at 20", () => {
    expect(relevanceScore(row, "acme", null)).toBe(20);
  });

  it("scores nodeType-contains at 10", () => {
    expect(relevanceScore(row, "story", null)).toBe(10);
  });

  it("falls back to 1 for content-only matches", () => {
    expect(relevanceScore(row, "unrelated", null)).toBe(1);
  });

  it("normalizes multi-term queries (average, not sum)", () => {
    // "pricing" (100) + "unrelated" (1) → 50.5
    expect(relevanceScore(row, "pricing unrelated", null)).toBe(50.5);
  });

  it("strips the * wrapping the query pool adds", () => {
    expect(relevanceScore(row, "*pricing*", null)).toBe(100);
  });

  it("adds the proximity boost for a context path", () => {
    const near = relevanceScore(row, "pricing", "ACME/Pricing");
    expect(near).toBeGreaterThan(100);
  });
});

describe("proximityBoost (PathProximity.ComputeBoost port)", () => {
  it("returns 0 without a context path", () => {
    expect(proximityBoost(null, "A/B")).toBe(0);
    expect(proximityBoost("", "A/B")).toBe(0);
  });

  it("computes MaxBoost/(1+segmentDistance)", () => {
    // context a/b/c vs result a/b/d: LCP 2 → distance (3-2)+(3-2)=2 → 40/3
    expect(proximityBoost("a/b/c", "a/b/d")).toBeCloseTo(40 / 3);
    // identical paths: distance 0 → full boost
    expect(proximityBoost("a/b", "a/b")).toBe(40);
  });

  it("compares segments case-insensitively", () => {
    expect(proximityBoost("A/B", "a/b")).toBe(40);
  });
});

describe("splitRemainder (the {area}/{id} URL split)", () => {
  it("maps empty to the default area", () => {
    expect(splitRemainder(null)).toEqual({ area: "", id: "" });
    expect(splitRemainder("")).toEqual({ area: "", id: "" });
  });

  it("maps a single segment to the area", () => {
    expect(splitRemainder("Activity")).toEqual({ area: "Activity", id: "" });
  });

  it("maps the rest to the id", () => {
    expect(splitRemainder("Settings/DataSources/x")).toEqual({ area: "Settings", id: "DataSources/x" });
  });

  it("tolerates surrounding slashes", () => {
    expect(splitRemainder("/Activity/")).toEqual({ area: "Activity", id: "" });
  });
});

describe("flattenMenuItems (PortalLayoutBase.FlattenMenuItems port)", () => {
  const leaf = (label: string): MenuItemDef => ({ label, area: label });

  it("returns flat lists unchanged", () => {
    const items = [leaf("A"), leaf("B")];
    expect(flattenMenuItems(items)).toBe(items);
  });

  it("inlines children behind a separator", () => {
    const items: MenuItemDef[] = [leaf("A"), { label: "Parent", area: "P", children: [leaf("C1"), leaf("C2")] }];
    const flat = flattenMenuItems(items);
    expect(flat.map((i) => i.area)).toEqual(["A", "_separator", "C1", "C2"]);
  });

  it("emits no leading separator when the parent comes first", () => {
    const items: MenuItemDef[] = [{ label: "Parent", area: "P", children: [leaf("C1")] }, leaf("A")];
    expect(flattenMenuItems(items).map((i) => i.area)).toEqual(["C1", "A"]);
  });
});

describe("initialsOf (UserProfile.GetInitials port)", () => {
  it("uses the first letter for single words", () => {
    expect(initialsOf("roland")).toBe("R");
  });

  it("uses first + last word initials for full names", () => {
    expect(initialsOf("Roland Maria Bürgi")).toBe("RB");
  });

  it("handles blanks", () => {
    expect(initialsOf("  ")).toBe("");
  });
});
