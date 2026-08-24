import { describe, expect, it } from "vitest";
import { composeDeployment, composePacks } from "./composePacks.js";
import type { LeafPack } from "./registryContext.js";

const A = () => null;
const B = () => null;
const C = () => null;
const base: LeafPack = {
  controls: { Markdown: A, Button: A },
  skins: { LayoutStack: A },
  fallback: A,
  defaultContainer: A,
};

describe("composePacks — the one layering rule for deployment-injected modules", () => {
  it("adds a module's leaves without touching the rest of the base", () => {
    const composed = composePacks(base, { controls: { ChessBoard: B } });
    expect(composed.controls.ChessBoard).toBe(B);
    expect(composed.controls.Markdown).toBe(A);
    expect(composed.fallback).toBe(A);
  });

  it("later contributions WIN — a deployment overriding a base leaf is deliberate", () => {
    const composed = composePacks(base, { controls: { Button: B } }, { controls: { Button: C } });
    expect(composed.controls.Button).toBe(C);
  });

  it("fallback/defaultContainer override only when a module actually provides them", () => {
    const composed = composePacks(base, { controls: { X: B } }, { fallback: C });
    expect(composed.fallback).toBe(C);
    expect(composed.defaultContainer).toBe(A);
  });

  it("null/undefined extensions are skipped — an unresolved module never breaks the pack", () => {
    expect(composePacks(base, undefined, null).controls.Markdown).toBe(A);
  });

  it("composeDeployment folds the manifest's modules in declaration order", () => {
    const composed = composeDeployment(base, [
      { name: "chess", pack: { controls: { ChessBoard: B } } },
      { name: "charts", pack: { skins: { Heatmap: C } } },
      { name: "packless" },
    ]);
    expect(composed.controls.ChessBoard).toBe(B);
    expect(composed.skins.Heatmap).toBe(C);
  });
});
