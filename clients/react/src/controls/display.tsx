import type { ReactNode } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Badge, Text, Tooltip, MessageBar, MessageBarBody, MessageBarTitle } from "@fluentui/react-components";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { controlStyle } from "../render/style.js";
import { str, useClick, useText } from "./common.js";
import { resolveIconByName } from "./icon.js";

function typo(t: string): { size: 100 | 200 | 300 | 400 | 500 | 600 | 700 | 800 | 900; weight: "regular" | "semibold" | "bold" } {
  switch (String(t || "").toLowerCase()) {
    case "herotitle":
    case "pagetitle":
    case "h1":
      return { size: 800, weight: "bold" };
    case "header":
    case "h2":
      return { size: 700, weight: "bold" };
    case "paneheader":
    case "subject":
    case "h3":
      return { size: 500, weight: "semibold" };
    case "h4":
      return { size: 400, weight: "semibold" };
    case "h5":
    case "h6":
      return { size: 300, weight: "semibold" };
    default:
      return { size: 300, weight: "regular" };
  }
}

function Label({ control }: { control: UiControl }): ReactNode {
  const value = useText(control.data);
  const onClick = useClick(control);
  const t = typo(str(useResolve(control.typo)));
  return (
    <Text
      as="span"
      size={t.size}
      weight={(useResolve(control.weight) as any) ?? t.weight}
      onClick={onClick}
      style={{ cursor: onClick ? "pointer" : undefined, ...controlStyle(control) }}
    >
      {value}
    </Text>
  );
}

function BadgeView({ control }: { control: UiControl }): ReactNode {
  return <Badge appearance="filled" color="brand">{useText(control.data)}</Badge>;
}

function IconView({ control }: { control: UiControl }): ReactNode {
  const name = useText(control.data);
  const onClick = useClick(control);
  const Cmp = resolveIconByName(name);
  if (Cmp) return <Cmp onClick={onClick} style={{ cursor: onClick ? "pointer" : undefined }} />;
  // URL or unknown name → render as image / text fallback
  if (/^https?:|^data:|\.(svg|png|jpg|jpeg|gif|webp)$/i.test(name))
    return <img src={name} alt="" width={20} height={20} onClick={onClick} />;
  return <span onClick={onClick}>{name}</span>;
}

function HtmlView({ control }: { control: UiControl }): ReactNode {
  return <div style={controlStyle(control)} dangerouslySetInnerHTML={{ __html: useText(control.data) }} />;
}

function MarkdownView({ control }: { control: UiControl }): ReactNode {
  return (
    <div className="mw-markdown" style={controlStyle(control)}>
      <Markdown remarkPlugins={[remarkGfm]}>{useText(control.data)}</Markdown>
    </div>
  );
}

function CodeSample({ control }: { control: UiControl }): ReactNode {
  return (
    <pre
      style={{
        background: "var(--colorNeutralBackground3)",
        padding: 12,
        borderRadius: 4,
        overflow: "auto",
        fontFamily: "var(--fontFamilyMonospace)",
        fontSize: 13,
      }}
    >
      <code>{useText(control.data)}</code>
    </pre>
  );
}

function ExceptionView({ control }: { control: UiControl }): ReactNode {
  const message = useText(control.message);
  const type = useText(control.type);
  const stack = useText(control.stackTrace);
  return (
    <MessageBar intent="error">
      <MessageBarBody>
        <MessageBarTitle>{type || "Error"}</MessageBarTitle>
        {message}
        {stack ? <pre style={{ whiteSpace: "pre-wrap", fontSize: 12, marginTop: 4 }}>{stack}</pre> : null}
      </MessageBarBody>
    </MessageBar>
  );
}

function Spacer(): ReactNode {
  return <div style={{ flex: 1 }} />;
}

export const displayControls = {
  Label,
  Badge: BadgeView,
  Icon: IconView,
  Html: HtmlView,
  Markdown: MarkdownView,
  CodeSample,
  Highlight: CodeSample,
  Exception: ExceptionView,
  Spacer,
};

export { Tooltip };
