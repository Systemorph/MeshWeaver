import type { ReactNode } from "react";
import { Link } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { useClick, useText } from "./common.js";
import { resolveIconByName } from "./icon.js";

function NavLink({ control }: { control: UiControl }): ReactNode {
  const title = useText(control.title);
  const link = useMeshLink(useText(control.url) || undefined);
  const emitClick = useClick(control);
  const active = !!useResolve(control.isActive);
  const Icon = resolveIconByName(useText(control.icon));
  return (
    <Link
      href={link.href}
      onClick={(e) => {
        emitClick?.();
        link.onClick?.(e);
      }}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 6,
        padding: "4px 8px",
        borderRadius: 4,
        fontWeight: active ? 600 : 400,
        background: active ? "var(--colorNeutralBackground1Selected)" : undefined,
        textDecoration: "none",
      }}
    >
      {Icon ? <Icon /> : null}
      {title}
    </Link>
  );
}

export const navControls = { NavLink };
