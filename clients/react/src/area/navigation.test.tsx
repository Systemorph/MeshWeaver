// The navigation seam (navigation.tsx) — hosts map app-absolute mesh targets onto their router
// (portal-next: basePath + Next router; the SPA: "#/{path}" hash routes). These tests pin the
// contract: hrefs render through hrefFor, plain left-clicks route through navigate (and are
// preventDefault-ed), modified clicks and external targets fall through to the browser, and the
// HTML interceptor routes anchors inside injected (non-React) markup.

import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { NavigationProvider, useHtmlLinkInterceptor, useMeshLink, type MeshNavigation } from "./navigation.js";

function TestLink({ target }: { target?: string }): ReactNode {
  const link = useMeshLink(target);
  return (
    <a href={link.href} onClick={link.onClick}>
      go
    </a>
  );
}

function InjectedHtml({ html }: { html: string }): ReactNode {
  const onClick = useHtmlLinkInterceptor();
  return <div onClick={onClick} dangerouslySetInnerHTML={{ __html: html }} />;
}

function host(): { navigation: MeshNavigation; navigate: ReturnType<typeof vi.fn> } {
  const navigate = vi.fn();
  return { navigation: { hrefFor: (t) => `/base${t}`, navigate }, navigate };
}

describe("useMeshLink", () => {
  it("renders the host href and routes a left-click through navigate", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <TestLink target="/Doc/GUI" />
      </NavigationProvider>,
    );
    const anchor = screen.getByText("go");
    expect(anchor.getAttribute("href")).toBe("/base/Doc/GUI");
    fireEvent.click(anchor);
    expect(navigate).toHaveBeenCalledWith("/Doc/GUI");
  });

  it("leaves modified clicks (new-tab) to the browser", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <TestLink target="/Doc/GUI" />
      </NavigationProvider>,
    );
    fireEvent.click(screen.getByText("go"), { ctrlKey: true });
    fireEvent.click(screen.getByText("go"), { metaKey: true });
    fireEvent.click(screen.getByText("go"), { button: 1 });
    expect(navigate).not.toHaveBeenCalled();
  });

  it("passes external targets through untouched", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <TestLink target="https://example.com/x" />
      </NavigationProvider>,
    );
    const anchor = screen.getByText("go");
    expect(anchor.getAttribute("href")).toBe("https://example.com/x");
    fireEvent.click(anchor);
    expect(navigate).not.toHaveBeenCalled();
  });

  it("is inert without a target and root-absolute without a provider", () => {
    render(<TestLink />);
    expect(screen.getByText("go").getAttribute("href")).toBeNull();
    render(<TestLink target="/Doc/GUI" />);
    expect(screen.getAllByText("go")[1].getAttribute("href")).toBe("/Doc/GUI");
  });
});

describe("useHtmlLinkInterceptor — anchors inside injected HTML", () => {
  it("routes internal anchors through navigate and prevents the default load", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <InjectedHtml html='<p>see <a href="/Doc/GUI/Sibling">the sibling</a></p>' />
      </NavigationProvider>,
    );
    fireEvent.click(screen.getByText("the sibling"));
    expect(navigate).toHaveBeenCalledWith("/Doc/GUI/Sibling");
  });

  it("skips external, targeted, and download anchors", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <InjectedHtml
          html={
            '<a href="https://example.com">ext</a>' +
            '<a href="/Doc/X" target="_blank">blank</a>' +
            '<a href="/Doc/Y" download>dl</a>' +
            '<a href="#frag">frag</a>'
          }
        />
      </NavigationProvider>,
    );
    fireEvent.click(screen.getByText("ext"));
    fireEvent.click(screen.getByText("blank"));
    fireEvent.click(screen.getByText("dl"));
    fireEvent.click(screen.getByText("frag"));
    expect(navigate).not.toHaveBeenCalled();
  });
});
