import type { ReactNode } from "react";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { useClick, useText } from "./common.js";
import { MeshIcon } from "./MeshIcon.js";

// Styling lives in the shared stylesheet (meshStyles.ts .mw-nav-link rules) so hover/active
// states match Blazor's FluentNavLink; the component only maps the control to an anchor.
function NavLink({ control }: { control: UiControl }): ReactNode {
  const title = useText(control.title);
  const link = useMeshLink(useText(control.url) || undefined);
  const emitClick = useClick(control);
  const active = !!useResolve(control.isActive);
  // Pass the RESOLVED raw value (a name string, URL, inline SVG, emoji OR a FluentIcon
  // {provider,id,…} object) — MeshIcon classifies every shape; nav icons in the mesh are
  // typically /static/NodeTypeIcons/*.svg URLs or emoji, which the old name-only resolver dropped.
  const icon = useResolve(control.icon);
  return (
    <a
      href={link.href}
      className={active ? "mw-nav-link mw-nav-link-active" : "mw-nav-link"}
      onClick={(e) => {
        emitClick?.();
        link.onClick?.(e);
      }}
    >
      <MeshIcon value={icon} size={20} />
      <span className="mw-nav-link-text">{title}</span>
    </a>
  );
}

export const navControls = { NavLink };
