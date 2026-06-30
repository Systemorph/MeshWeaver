import type { ReactNode } from "react";
import { Text } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { displayControls } from "../controls/display.js";
import { inputControls } from "../controls/inputs.js";
import { dataControls } from "../controls/data.js";
import { navControls } from "../controls/nav.js";
import { feedbackControls } from "../controls/feedback.js";
import { containerControls } from "../controls/containers.js";
import { editorControls } from "../controls/editors.js";
import { meshControls } from "../controls/mesh.js";

export type ControlComponent = (props: { control: UiControl }) => ReactNode;

/** `$type` → React component. Spread your own entries to extend or override. */
export const controlRegistry: Record<string, ControlComponent> = {
  ...displayControls,
  ...inputControls,
  ...dataControls,
  ...navControls,
  ...feedbackControls,
  ...containerControls,
  ...editorControls,
  ...meshControls,
};

export function FallbackControl({ control }: { control: UiControl }): ReactNode {
  return (
    <Text italic size={200} style={{ color: "var(--colorPaletteRedForeground1)" }}>
      Unsupported control: {control.$type}
    </Text>
  );
}
