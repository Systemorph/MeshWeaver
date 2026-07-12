// Pins the ONE icon-value decision table (classifyIcon) every pack and shell dispatches through —
// the divergent per-call-site classifications were why nav/search dropped URL and emoji icons
// ("most SVGs are not showing").

import { describe, expect, it } from "vitest";
import { classifyIcon, iconForRendering, isEmojiIcon, isIconUrl } from "./iconValue.js";

describe("classifyIcon", () => {
  it("classifies inline SVG documents", () => {
    expect(classifyIcon('<svg viewBox="0 0 10 10"><circle/></svg>').kind).toBe("svg");
  });

  it("classifies image URLs, static paths and data URIs", () => {
    expect(classifyIcon("/static/NodeTypeIcons/person.svg")).toEqual({ kind: "url", text: "/static/NodeTypeIcons/person.svg" });
    expect(classifyIcon("https://example.com/x.png").kind).toBe("url");
    expect(classifyIcon("data:image/svg+xml,<svg/>").kind).toBe("url");
    expect(classifyIcon("images/avatar.webp").kind).toBe("url");
  });

  it("classifies emoji and short glyphs", () => {
    expect(classifyIcon("🚀")).toEqual({ kind: "emoji", text: "🚀" });
    expect(classifyIcon("⚙️").kind).toBe("emoji");
  });

  it("classifies Fluent names — bare (any case), prefixed, and the serialized {provider,id} object", () => {
    expect(classifyIcon("Save")).toEqual({ kind: "fluent", text: "Save" });
    expect(classifyIcon("save").kind).toBe("fluent"); // curated-map keys are lowercase
    expect(classifyIcon("fluent:ArrowSync").kind).toBe("fluent");
    expect(classifyIcon({ provider: "fluent-ui", id: "Document" })).toEqual({ kind: "fluent", text: "Document" });
  });

  it("classifies empty / null / unusable objects as none", () => {
    expect(classifyIcon(null).kind).toBe("none");
    expect(classifyIcon("").kind).toBe("none");
    expect(classifyIcon({}).kind).toBe("none");
  });
});

describe("node-icon helpers", () => {
  it("iconForRendering filters legacy Fluent names but keeps SVGs/URLs/emoji (GetIconForRendering parity)", () => {
    expect(iconForRendering("Document")).toBeNull();
    expect(iconForRendering("ArrowLeft")).toBeNull();
    // EXACT server semantics (MeshNodeImageHelper.IsFluentIconName): only UPPERCASE-start,
    // letters-only values are legacy names — lowercase-start or digit-carrying values pass through.
    expect(iconForRendering("save")).toBe("save");
    expect(iconForRendering("Abc123")).toBe("Abc123");
    expect(iconForRendering("/static/NodeTypeIcons/code.svg")).toBe("/static/NodeTypeIcons/code.svg");
    expect(iconForRendering("🧠")).toBe("🧠");
    expect(iconForRendering(null)).toBeNull();
  });

  it("isEmojiIcon rejects names, paths and long strings", () => {
    expect(isEmojiIcon("🚀")).toBe(true);
    expect(isEmojiIcon("Save")).toBe(false);
    expect(isEmojiIcon("/static/x.svg")).toBe(false);
    expect(isEmojiIcon("a-longer-string")).toBe(false);
  });

  it("isIconUrl accepts rooted paths and extensions", () => {
    expect(isIconUrl("/static/NodeTypeIcons/space.svg")).toBe(true);
    expect(isIconUrl("Document")).toBe(false);
  });
});
