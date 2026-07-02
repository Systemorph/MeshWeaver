import type { ReactNode } from "react";
import { Text } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import type { LeafPack, ControlComponent } from "./registryContext.js";
import { skinRegistry, DefaultStackSkin } from "./skins.js";
import { displayControls } from "../controls/display.js";
import { inputControls } from "../controls/inputs.js";
import { dataControls } from "../controls/data.js";
import { chartControls } from "../controls/chart.js";
import { pivotControls } from "../controls/pivot.js";
import { navControls } from "../controls/nav.js";
import { feedbackControls } from "../controls/feedback.js";
import { containerControls } from "../controls/containers.js";
import { editorControls } from "../controls/editors.js";
import { meshControls } from "../controls/mesh.js";
import { appearanceControls } from "../controls/appearance.js";
import { itemTemplateControls } from "../controls/itemTemplate.js";

export type { ControlComponent };

/** `$type` → React component. Spread your own entries to extend or override. */
export const controlRegistry: Record<string, ControlComponent> = {
  ...displayControls,
  ...inputControls,
  ...dataControls,
  ...chartControls,
  ...pivotControls,
  ...navControls,
  ...feedbackControls,
  ...containerControls,
  ...editorControls,
  ...meshControls,
  ...appearanceControls,
  ...itemTemplateControls,
};

export function FallbackControl({ control }: { control: UiControl }): ReactNode {
  return (
    <Text italic size={200} style={{ color: "var(--colorPaletteRedForeground1)" }}>
      Unsupported control: {control.$type}
    </Text>
  );
}

/** The Fluent UI (web/DOM) leaf pack — what `<MeshAreaView>` installs into the renderer. */
export const fluentPack: LeafPack = {
  controls: controlRegistry,
  skins: skinRegistry,
  fallback: FallbackControl,
  defaultContainer: DefaultStackSkin,
};
