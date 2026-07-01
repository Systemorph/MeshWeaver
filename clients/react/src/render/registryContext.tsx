// The seam that makes the renderer pack-swappable. The platform-agnostic core (ControlRenderer +
// area/*) dispatches on $type and pops skins, but never imports a concrete component — it pulls the
// "leaf pack" (control + skin components) from this context. The web entry supplies a Fluent DOM pack;
// a React Native app supplies a `<View>`/`<Text>` pack. Same UiControl tree, swappable leaves — the
// direct analog of MAUI having a native MauiViewPack and Blazor a web pack.

import { createContext, useContext } from "react";
import type { ReactNode } from "react";
import type { Skin, UiControl } from "../area/types.js";

export type ControlComponent = (props: { control: UiControl }) => ReactNode;
export type SkinComponent = (props: { skin: Skin; control: UiControl }) => ReactNode;

export interface LeafPack {
  /** `$type` → control component (leaf controls). */
  controls: Record<string, ControlComponent>;
  /** skin `$type` → wrapper component (Stack/Grid/Tabs/Card/…); `__default` handles unknown skins. */
  skins: Record<string, SkinComponent>;
  /** Renders an unknown control `$type`. */
  fallback: ControlComponent;
  /** Wrapper for a container with no (remaining) skin — the default layout (a flex Stack / RN View). */
  defaultContainer: SkinComponent;
}

const PackContext = createContext<LeafPack | null>(null);

export function useLeafPack(): LeafPack {
  const pack = useContext(PackContext);
  if (!pack)
    throw new Error(
      "MeshWeaver control rendered without a leaf pack. Use <MeshAreaView> (web/Fluent), or wrap with <RegistryProvider pack={…}> supplying your own pack.",
    );
  return pack;
}

export function RegistryProvider({ pack, children }: { pack: LeafPack; children: ReactNode }) {
  return <PackContext.Provider value={pack}>{children}</PackContext.Provider>;
}
