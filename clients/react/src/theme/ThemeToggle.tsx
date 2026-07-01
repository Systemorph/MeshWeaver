// Header theme switcher — the React counterpart of the Blazor portal's theme selector in the
// SiteSettingsPanel: the same three DesignThemeModes (Light / Dark / System), persisted under the
// same localStorage key via useThemeMode.

import type { ReactNode } from "react";
import {
  Button,
  Menu,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Tooltip,
  type MenuProps,
} from "@fluentui/react-components";
import { Desktop20Regular, WeatherMoon20Regular, WeatherSunny20Regular } from "@fluentui/react-icons";
import { useThemeMode, type ThemeMode } from "./themeMode.js";

export interface ThemeToggleProps {
  /** Override the localStorage key (defaults to the Blazor-compatible "theme"). */
  storageKey?: string;
}

const modeIcons: Record<ThemeMode, JSX.Element> = {
  light: <WeatherSunny20Regular />,
  dark: <WeatherMoon20Regular />,
  system: <Desktop20Regular />,
};

/** A menu button cycling the theme mode: Light / Dark / System (the current mode is checked). */
export function ThemeToggle({ storageKey }: ThemeToggleProps): ReactNode {
  const { mode, resolved, setMode } = useThemeMode({ storageKey });
  const onChange: MenuProps["onCheckedValueChange"] = (_, data) => {
    const next = data.checkedItems[0] as ThemeMode | undefined;
    if (next) setMode(next);
  };
  return (
    <Menu checkedValues={{ mode: [mode] }} onCheckedValueChange={onChange}>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={`Theme: ${mode}`} relationship="label">
          <Button appearance="subtle" aria-label="Change theme" icon={resolved === "dark" ? modeIcons.dark : modeIcons.light} />
        </Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItemRadio name="mode" value="light" icon={modeIcons.light}>
            Light
          </MenuItemRadio>
          <MenuItemRadio name="mode" value="dark" icon={modeIcons.dark}>
            Dark
          </MenuItemRadio>
          <MenuItemRadio name="mode" value="system" icon={modeIcons.system}>
            System
          </MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
