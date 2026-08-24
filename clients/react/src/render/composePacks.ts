// Pack COMPOSITION — the one rule for layering module-contributed leaves over a base pack.
//
// The JS bundles are static (Metro/Hermes load no code at runtime), so modules cannot inject client
// leaves into a RUNNING app the way their server halves join the mesh. The composition point is the
// DEPLOYMENT: it declares which client modules ship in ITS bundle, the build resolves them as
// ordinary static imports, and this helper folds their contributions over the base pack. Server-
// declared controls need none of this — every standard control renders through the base pack
// already; a module reaches for a client extension only when it genuinely needs a bespoke LEAF
// (a chess board, a chart type the pack lacks).
//
// Later extensions win (a deployment deliberately overriding a base leaf is a feature, not a
// clash), and `fallback`/`defaultContainer` override only when an extension actually provides
// them — so composing never loses the base pack's safety nets.

import type { ControlComponent, LeafPack, SkinComponent } from "./registryContext.js";

/** What a client module contributes to a pack — both maps optional and additive. */
export interface PackExtension {
  controls?: Record<string, ControlComponent>;
  skins?: Record<string, SkinComponent>;
  fallback?: ControlComponent;
  defaultContainer?: SkinComponent;
}

/**
 * A DEPLOYMENT MODULE — the unit a deployment manifest injects (see
 * MeshWeaver.Plugins/app/react-native/deployment/). One object per module, deliberately open: `pack` is the slot
 * implemented today; new client extension points (screens, menu items, speech providers) get their
 * own named slots here as the seams land, so a module's shape never has to break.
 */
export interface DeploymentModule {
  /** Diagnostic name (shown when a module fails to compose). */
  name?: string;
  /** Control/skin leaves folded over the base pack. */
  pack?: PackExtension;
}

/** Fold `extensions` over `base`, left to right — later contributions win. */
export function composePacks(base: LeafPack, ...extensions: (PackExtension | undefined | null)[]): LeafPack {
  let result = base;
  for (const ext of extensions) {
    if (!ext) continue;
    result = {
      controls: { ...result.controls, ...(ext.controls ?? {}) },
      skins: { ...result.skins, ...(ext.skins ?? {}) },
      fallback: ext.fallback ?? result.fallback,
      defaultContainer: ext.defaultContainer ?? result.defaultContainer,
    };
  }
  return result;
}

/** Compose a base pack with a deployment's modules (the manifest's `modules` list, resolved). */
export function composeDeployment(base: LeafPack, modules: readonly DeploymentModule[]): LeafPack {
  return composePacks(base, ...modules.map((m) => m.pack));
}
