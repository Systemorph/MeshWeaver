// Pins the ONE icon-value decision table (classifyIcon) every pack and shell dispatches through —
// the divergent per-call-site classifications were why nav/search dropped URL and emoji icons
// ("most SVGs are not showing").

import { describe, expect, it } from "vitest";
import { backplatePalette, classifyIcon, ensureBackplate, hasBackplate, iconForRendering, isEmojiIcon, isIconUrl } from "./iconValue.js";

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

// Mirrors the server's IconBackplateTest (MeshWeaver.Graph.Test) — the policy is shared and the
// two implementations must not drift: same detection, same palette, same deterministic hue.
describe("ensureBackplate", () => {
  const monochrome =
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 4h16v16H4z"/></svg>';
  const plated =
    "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' rx='5' fill='#4338CA'/><path d='M10 5.5V10' stroke='#fff'/></svg>";

  it("leaves an authored plate untouched", () => {
    expect(ensureBackplate(plated)).toBe(plated);
  });

  it("leaves a thread identicon (full-bleed rect on its own canvas) untouched", () => {
    const identicon =
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="100" height="100" fill="#e8f4fd"/><rect x="20" y="20" width="20" height="20" fill="#0078d4"/></svg>';
    expect(ensureBackplate(identicon)).toBe(identicon);
  });

  it("plates a monochrome outline and recolors currentColor to white", () => {
    const out = ensureBackplate(monochrome);
    expect(out.startsWith("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' rx='5'")).toBe(true);
    expect(out).not.toContain("currentColor");
    expect(out).toContain('stroke="#fff"');
    expect(out).toContain("<svg x='3' y='3' width='18' height='18'");
    expect(out).toContain('viewBox="0 0 24 24"'); // the glyph keeps its own coordinate system
  });

  it("is deterministic and draws its hue from the shared palette", () => {
    const one = ensureBackplate(monochrome);
    expect(ensureBackplate(monochrome)).toBe(one);
    expect(backplatePalette.some((hue) => one.includes(`fill='${hue}'`))).toBe(true);
  });

  it("does not mistake rx or a small ornamental rect for a plate", () => {
    expect(hasBackplate("<svg viewBox='0 0 24 24'><rect x='9' y='9' width='6' height='6' fill='#333'/></svg>")).toBe(false);
    // rx='5' on an authored plate must not be read as x='5' (the C# bug this pins).
    expect(hasBackplate(plated)).toBe(true);
  });

  it("does not count a fill='none' full-canvas rect as a plate", () => {
    expect(hasBackplate("<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='none' stroke='currentColor'/></svg>")).toBe(false);
  });

  it("classifyIcon routes inline svg through the plate", () => {
    const out = classifyIcon(monochrome);
    expect(out.kind).toBe("svg");
    expect(out.text).toContain("<rect width='24' height='24' rx='5'");
  });
});
