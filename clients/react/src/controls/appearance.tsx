// The Appearance control — Blazor renders this as AppearanceView: the theme settings panel
// (mode select persisted in localStorage["theme"], reset button). This is the React mirror of the
// same panel, bound to the same storage via useThemeMode. Accent color / text direction picking
// (Blazor's OfficeColor + Direction) are not ported yet — see the parity ratchet.

import type { ReactNode } from "react";
import { useState } from "react";
import { Button, Divider, Dropdown, Field, Option, Text } from "@fluentui/react-components";
import { clearStoredTheme, useThemeMode, type ThemeMode } from "../theme/themeMode.js";

const modes: { value: ThemeMode; text: string }[] = [
  { value: "system", text: "System" },
  { value: "light", text: "Light" },
  { value: "dark", text: "Dark" },
];

function AppearanceView(): ReactNode {
  const { mode, setMode } = useThemeMode();
  const [status, setStatus] = useState("");
  const current = modes.find((m) => m.value === mode) ?? modes[0];
  return (
    <div style={{ maxWidth: 500, display: "flex", flexDirection: "column", gap: 16 }}>
      <Field label="Theme">
        <Dropdown
          value={current.text}
          selectedOptions={[current.value]}
          onOptionSelect={(_, d) => {
            if (d.optionValue) {
              setMode(d.optionValue as ThemeMode);
              setStatus("");
            }
          }}
        >
          {modes.map((m) => (
            <Option key={m.value} value={m.value}>
              {m.text}
            </Option>
          ))}
        </Dropdown>
      </Field>
      <Text size={200}>These values are persisted in LocalStorage and will be recovered during your next visits.</Text>
      <Divider />
      <div>
        <Button
          onClick={() => {
            clearStoredTheme();
            setStatus("Settings reset!");
          }}
        >
          Reset settings
        </Button>
      </div>
      {status ? (
        <Text size={200} italic weight="bold">
          {status}
        </Text>
      ) : null}
    </div>
  );
}

export const appearanceControls = {
  Appearance: AppearanceView,
};
