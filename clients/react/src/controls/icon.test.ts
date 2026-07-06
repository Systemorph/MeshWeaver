import { describe, expect, it } from "vitest";
import { resolveIconByName } from "./icon.js";

// Pins Fix 3: the icon resolver widened to cover the nav/settings/toolbar names AND to accept the
// framework's FluentIcon value shape ({ provider, id, size, variant }) — the serialized
// MeshWeaver.Domain.Icon that nav/group/toolbar icon props carry. Before, the object stringified to
// "[object Object]" and every such icon rendered blank.

describe("resolveIconByName", () => {
  it("resolves curated names (case/variant/prefix insensitive)", () => {
    for (const name of ["Save", "save", "fluent:Save", "Save20Regular"]) expect(resolveIconByName(name), name).toBeTruthy();
  });

  it("resolves the widened nav/settings/toolbar icon names", () => {
    for (const name of ["Shield", "ShieldKeyhole", "Key", "ArrowSync", "Folder", "PaintBrush", "ArrowLeft", "Info", "Bookmark", "Sparkle", "Database", "Bot"])
      expect(resolveIconByName(name), name).toBeTruthy();
  });

  it("resolves useful aliases (sync, back, paint, refresh, import)", () => {
    for (const name of ["sync", "back", "paint", "refresh", "import"]) expect(resolveIconByName(name), name).toBeTruthy();
  });

  it("resolves the FluentIcon object shape via its id (either casing)", () => {
    expect(resolveIconByName({ provider: "fluent-ui", id: "Shield", size: 24, variant: "Regular" })).toBe(resolveIconByName("Shield"));
    expect(resolveIconByName({ Provider: "fluent-ui", Id: "ArrowSync" })).toBe(resolveIconByName("ArrowSync"));
  });

  it("falls back to undefined (never crashes) for unmapped / malformed values", () => {
    expect(resolveIconByName("SomeIconThatDoesNotExist")).toBeUndefined();
    expect(resolveIconByName({ provider: "fluent-ui", id: "NopeNotReal" })).toBeUndefined();
    expect(resolveIconByName({ provider: "fluent-ui" })).toBeUndefined();
    expect(resolveIconByName(null)).toBeUndefined();
    expect(resolveIconByName("")).toBeUndefined();
  });
});
