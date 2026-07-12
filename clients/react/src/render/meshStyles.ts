// The renderer's ONE injected stylesheet — everything a layout area needs beyond per-control
// inline styles, shared by every web shell (Vite SPA, portal-next, Electron). Injected once by
// <MeshAreaView>; shells add NOTHING.
//
// Three concerns live here:
//
// 1. FAST design-token ALIASES. Server-side layout areas emit inline styles written for the
//    Blazor portal, whose FluentUI *Web Components* define FAST tokens (--neutral-stroke-rest,
//    --neutral-foreground-hint, --accent-fill-rest, …). Fluent UI *React v9* defines a different
//    token set (--colorNeutralStroke1, …), so in the React clients every such var() silently
//    resolved to nothing — invisible <hr>s, missing hint colors, transparent backgrounds ("the
//    styling looks broken"). The alias block maps each FAST token the server emits (grep
//    `var\(--` in src/MeshWeaver.Graph + src/MeshWeaver.Layout) onto its v9 equivalent; v9
//    tokens flip with the FluentProvider theme, so dark mode comes free.
//
// 2. Markdown typography — the SAME vendored github-markdown CSS the Blazor portal ships
//    (src/MeshWeaver.Blazor/wwwroot/css/github-markdown-*.css), re-scoped to .mw-markdown.
//
// 3. LayoutGrid item spans — the Blazor FluentGrid breakpoint classes (mobile-first: the value
//    set at the largest breakpoint at or below the viewport wins; unset ⇒ full row).

import { githubMarkdownCss, githubMarkdownDarkCss, scopeMarkdownCss } from "../theme/githubMarkdown.js";

/** FAST (FluentUI Web Components) token → Fluent React v9 token aliases, for the inline styles
 *  server-side layout areas emit. Declared on :root; var() indirection is late-bound, so each
 *  alias resolves against the v9 value in scope at the USE site (i.e. the FluentProvider theme). */
const FAST_TOKEN_ALIASES = `
:root {
  --accent-fill-rest: var(--colorBrandBackground);
  --accent-fill-hover: var(--colorBrandBackgroundHover);
  --accent-foreground-rest: var(--colorBrandForeground1);
  --accent-foreground-hover: var(--colorBrandForeground2);
  --foreground-on-accent-rest: var(--colorNeutralForegroundOnBrand);
  --neutral-foreground-rest: var(--colorNeutralForeground1);
  --neutral-foreground-hint: var(--colorNeutralForeground3);
  --neutral-fill-rest: var(--colorNeutralBackground1);
  --neutral-fill-hover: var(--colorNeutralBackground1Hover);
  --neutral-fill-secondary-rest: var(--colorNeutralBackground2);
  --neutral-fill-secondary-hover: var(--colorNeutralBackground2Hover);
  --neutral-fill-stealth-rest: var(--colorSubtleBackground);
  --neutral-fill-stealth-hover: var(--colorSubtleBackgroundHover);
  --neutral-layer-1: var(--colorNeutralBackground1);
  --neutral-layer-2: var(--colorNeutralBackground2);
  --neutral-layer-3: var(--colorNeutralBackground3);
  --neutral-layer-4: var(--colorNeutralBackground4);
  --neutral-layer-floating: var(--colorNeutralBackground1);
  --neutral-layer-card-container: var(--colorNeutralBackground1);
  --neutral-stroke-rest: var(--colorNeutralStroke1);
  --neutral-stroke-hover: var(--colorNeutralStroke1Hover);
  --neutral-stroke-divider-rest: var(--colorNeutralStroke2);
  --neutral-stroke-divider: var(--colorNeutralStroke2);
  --error-fill-rest: var(--colorStatusDangerBackground3);
  --error-foreground: var(--colorStatusDangerForeground1);
  --error-foreground-rest: var(--colorStatusDangerForeground1);
  --error-stroke-rest: var(--colorStatusDangerBorder1);
  --error: var(--colorStatusDangerForeground1);
  --warning-fill-rest: var(--colorStatusWarningBackground3);
  --warning-foreground: var(--colorStatusWarningForeground1);
  --warning-stroke-rest: var(--colorStatusWarningBorder1);
  --warning-color: var(--colorStatusWarningForeground1);
  --warning: var(--colorStatusWarningForeground1);
  --font-monospace: ui-monospace, "SF Mono", "Cascadia Code", Consolas, "Liberation Mono", monospace;
}
`;

/** Blazor FluentGrid breakpoints (fluentui-blazor): the min-width at which each tier applies. */
export const GRID_BREAKPOINTS: Record<string, number> = {
  xs: 0,
  sm: 600,
  md: 960,
  lg: 1280,
  xl: 1920,
  xxl: 2560,
};

function gridItemCss(): string {
  const rules: string[] = [
    // No span set anywhere → full row (the framework's own usage always starts at WithXs(12)).
    ".mw-grid-item{grid-column:span 12;min-width:0}",
  ];
  for (const [bp, minWidth] of Object.entries(GRID_BREAKPOINTS)) {
    const classes: string[] = [];
    for (let n = 1; n <= 12; n++) classes.push(`.mw-gi-${bp}-${n}{grid-column:span ${n}}`);
    rules.push(minWidth === 0 ? classes.join("") : `@media (min-width:${minWidth}px){${classes.join("")}}`);
  }
  return rules.join("\n");
}

const CHROME_CSS = `
/* Inline node icons (mw-inline-svg wraps sanitized <svg> markup) scale to their box. */
.mw-inline-svg > svg { width: 100%; height: 100%; }
/* Markdown flows in the Fluent type ramp — the vendored sheet sets GitHub's font; keep Fluent's. */
.mw-markdown { font-family: inherit; font-size: 14px; background: transparent; }
[data-theme="dark"] .mw-markdown { background: transparent; }
.mw-markdown table { display: table; width: 100%; }

/* Nav links — the FluentNavLink look: neutral text, hover wash, selected background + brand bar. */
.mw-nav-link {
  display: flex; align-items: center; gap: 8px;
  padding: 6px 10px; border-radius: 4px;
  color: var(--colorNeutralForeground1); text-decoration: none;
  font-size: 14px; line-height: 20px;
  position: relative;
}
.mw-nav-link:hover { background: var(--colorSubtleBackgroundHover); color: var(--colorNeutralForeground1); text-decoration: none; }
.mw-nav-link-active { background: var(--colorNeutralBackground1Selected); font-weight: 600; }
.mw-nav-link-active::before {
  content: ""; position: absolute; left: 0; top: 6px; bottom: 6px; width: 3px; border-radius: 2px;
  background: var(--colorCompoundBrandBackground);
}
.mw-nav-link-text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.mw-nav-group-header {
  display: flex; align-items: center; gap: 6px; cursor: pointer;
  padding: 6px 4px; border-radius: 4px;
}
.mw-nav-group-header:hover { background: var(--colorSubtleBackgroundHover); }
`;

export const MESH_STYLES_ID = "mw-mesh-styles";

/** The full stylesheet text (also used by tests / SSR string rendering). */
export function meshStylesText(): string {
  return [
    FAST_TOKEN_ALIASES,
    scopeMarkdownCss(githubMarkdownCss, ".mw-markdown"),
    scopeMarkdownCss(githubMarkdownDarkCss, ".mw-markdown"),
    CHROME_CSS,
    gridItemCss(),
  ].join("\n");
}

/** Inject the renderer stylesheet once per document (idempotent; SSR-safe no-op). */
export function ensureMeshStyles(): void {
  if (typeof document === "undefined") return;
  if (document.getElementById(MESH_STYLES_ID)) return;
  const style = document.createElement("style");
  style.id = MESH_STYLES_ID;
  style.textContent = meshStylesText();
  document.head.appendChild(style);
}
