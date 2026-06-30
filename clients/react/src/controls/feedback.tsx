import type { ReactNode } from "react";
import { ProgressBar, Spinner, Text } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useText } from "./common.js";

function ProgressView({ control }: { control: UiControl }): ReactNode {
  const message = useText(control.message);
  const p = Number(useResolve(control.progress));
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
      {message ? <Text size={200}>{message}</Text> : null}
      <ProgressBar value={Number.isFinite(p) ? Math.max(0, Math.min(1, p / 100)) : undefined} />
    </div>
  );
}

function SpinnerView({ control }: { control: UiControl }): ReactNode {
  return <Spinner size="tiny" label={useText(control.message) || useText(control.progressMessage) || undefined} />;
}

export const feedbackControls = {
  Progress: ProgressView,
  Spinner: SpinnerView,
};
