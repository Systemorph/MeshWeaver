// The navigation seam (navigation.tsx) — hosts map app-absolute mesh targets onto their router
// (portal-next: basePath + Next router; the SPA: "#/{path}" hash routes). These tests pin the
// contract: hrefs render through hrefFor, plain left-clicks route through navigate (and are
// preventDefault-ed), modified clicks and external targets fall through to the browser, and the
// HTML interceptor routes anchors inside injected (non-React) markup.

import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { NavigationProvider, normalizeTarget, useHtmlLinkInterceptor, useMeshLink, type MeshNavigation } from "./navigation.js";

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

  // Nav menus / NavLinks emit ADDRESS-RELATIVE hrefs with no leading slash (what
  // LayoutAreaReference.ToHref(address) produces, e.g. "roland/Settings/Metadata"). These are
  // internal mesh paths: they must render through hrefFor (with the host basePath) and route
  // client-side — NOT fall through as a bare relative <a> that the browser doubles onto the URL.
  it("treats a bare (no-leading-slash) mesh path as internal and routes it", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <TestLink target="roland/Settings/Metadata" />
      </NavigationProvider>,
    );
    const anchor = screen.getByText("go");
    expect(anchor.getAttribute("href")).toBe("/base/roland/Settings/Metadata");
    fireEvent.click(anchor);
    expect(navigate).toHaveBeenCalledWith("/roland/Settings/Metadata");
  });

  it("keeps genuinely external / non-routable targets external (no host href, no navigate)", () => {
    const cases = ["https://x.com", "//x.com", "#frag", "mailto:a@b", "tel:+1", "../Sibling"];
    for (const target of cases) {
      const { navigation, navigate } = host();
      const { unmount } = render(
        <NavigationProvider navigation={navigation}>
          <TestLink target={target} />
        </NavigationProvider>,
      );
      const anchor = screen.getByText("go");
      expect(anchor.getAttribute("href")).toBe(target); // unchanged — no basePath applied
      fireEvent.click(anchor);
      expect(navigate).not.toHaveBeenCalled();
      unmount();
    }
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

  it("routes a bare (no-leading-slash) mesh-path anchor client-side", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <InjectedHtml html='<a href="roland/Settings/Metadata">settings</a>' />
      </NavigationProvider>,
    );
    fireEvent.click(screen.getByText("settings"));
    expect(navigate).toHaveBeenCalledWith("/roland/Settings/Metadata");
  });

  it("skips external, targeted, download, and relative-path anchors", () => {
    const { navigation, navigate } = host();
    render(
      <NavigationProvider navigation={navigation}>
        <InjectedHtml
          html={
            '<a href="https://example.com">ext</a>' +
            '<a href="/Doc/X" target="_blank">blank</a>' +
            '<a href="/Doc/Y" download>dl</a>' +
            '<a href="#frag">frag</a>' +
            '<a href="mailto:a@b">mail</a>' +
            '<a href="../Sibling">rel</a>'
          }
        />
      </NavigationProvider>,
    );
    fireEvent.click(screen.getByText("ext"));
    fireEvent.click(screen.getByText("blank"));
    fireEvent.click(screen.getByText("dl"));
    fireEvent.click(screen.getByText("frag"));
    fireEvent.click(screen.getByText("mail"));
    fireEvent.click(screen.getByText("rel"));
    expect(navigate).not.toHaveBeenCalled();
  });
});

describe("normalizeTarget", () => {
  it("prepends a leading slash to bare mesh paths and leaves external/relative targets alone", () => {
    // Bare mesh paths → app-absolute
    expect(normalizeTarget("roland/Settings/Metadata")).toBe("/roland/Settings/Metadata");
    expect(normalizeTarget("Doc/GUI")).toBe("/Doc/GUI");
    expect(normalizeTarget("roland/Settings?tab=x")).toBe("/roland/Settings?tab=x");
    // Already app-absolute — untouched
    expect(normalizeTarget("/Doc/GUI")).toBe("/Doc/GUI");
    // Genuinely external / non-routable — untouched
    expect(normalizeTarget("https://x.com")).toBe("https://x.com");
    expect(normalizeTarget("//x.com")).toBe("//x.com");
    expect(normalizeTarget("#frag")).toBe("#frag");
    expect(normalizeTarget("mailto:a@b")).toBe("mailto:a@b");
    expect(normalizeTarget("tel:+1")).toBe("tel:+1");
    expect(normalizeTarget("../Sibling")).toBe("../Sibling");
    expect(normalizeTarget("./child")).toBe("./child");
  });
});
