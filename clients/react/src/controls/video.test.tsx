// Parity with Blazor's VideoView.razor and SlideShowView.razor (+ its .razor.js keyboard driver) —
// two controls the Blazor pack has shipped since #407 that the React pack was missing entirely.
//
// VideoView: `<video controls>` for VideoKind.Video (the default), `<iframe>` for VideoKind.Embed,
// both sized `width:100%` with the control's AspectRatio (default "16/9") on a black backdrop.
// SlideShowView: renders NO chrome — it binds the presenter keys to the hrefs it carries, and a
// null href makes that key a no-op.

/** @vitest-environment jsdom */
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";
import type { UiControl } from "../area/types.js";
import { NavigationProvider } from "../area/navigation.js";

function tree(control: UiControl): AreaTree {
  return { data: {}, areas: { main: control } };
}

describe("Video (Blazor VideoView parity)", () => {
  it("renders a native <video controls> with poster and aspect ratio by default", () => {
    const { container } = render(
      <MeshAreaView
        source={new StaticAreaSource(tree({ $type: "Video", src: "/media/lecture.mp4", poster: "/media/cover.jpg", title: "Lecture 1" }))}
        rootArea="main"
      />,
    );
    const video = container.querySelector("video");
    expect(video).toBeTruthy();
    expect(video!.getAttribute("src")).toBe("/media/lecture.mp4");
    expect(video!.getAttribute("poster")).toBe("/media/cover.jpg");
    expect(video!.getAttribute("title")).toBe("Lecture 1");
    expect(video!.hasAttribute("controls")).toBe(true);
    expect(video!.style.aspectRatio).toBe("16/9"); // the control's default
    expect(container.querySelector("iframe")).toBeNull();
  });

  it("renders an embed <iframe> for VideoKind.Embed, honouring an explicit aspect ratio", () => {
    const { container } = render(
      <MeshAreaView
        source={new StaticAreaSource(tree({ $type: "Video", src: "https://www.youtube.com/embed/x", kind: "Embed", aspectRatio: "4/3" }))}
        rootArea="main"
      />,
    );
    const frame = container.querySelector("iframe");
    expect(frame).toBeTruthy();
    expect(frame!.getAttribute("src")).toBe("https://www.youtube.com/embed/x");
    expect(frame!.getAttribute("allowfullscreen")).not.toBeNull();
    expect(frame!.style.aspectRatio).toBe("4/3");
    expect(container.querySelector("video")).toBeNull();
  });

  it("renders nothing when Src is empty (Blazor's guard)", () => {
    const { container } = render(<MeshAreaView source={new StaticAreaSource(tree({ $type: "Video", src: "" }))} rootArea="main" />);
    expect(container.querySelector("video")).toBeNull();
    expect(container.querySelector("iframe")).toBeNull();
  });
});

describe("SlideShow (Blazor SlideShowView parity)", () => {
  const deck = {
    $type: "SlideShow",
    firstHref: "/Deck/1",
    previousHref: "/Deck/2",
    nextHref: "/Deck/4",
    lastHref: "/Deck/9",
    exitHref: "/Deck",
  };

  function renderDeck(control: UiControl = deck) {
    const navigate = vi.fn();
    const result = render(
      <NavigationProvider navigation={{ hrefFor: (t) => t, navigate }}>
        <MeshAreaView source={new StaticAreaSource(tree(control))} rootArea="main" />
      </NavigationProvider>,
    );
    return { navigate, ...result };
  }

  it("renders no visible chrome", () => {
    const { container } = renderDeck();
    expect(container.textContent).toBe("");
  });

  it.each([
    ["ArrowRight", "/Deck/4"],
    ["ArrowDown", "/Deck/4"],
    ["PageDown", "/Deck/4"],
    [" ", "/Deck/4"],
    ["Enter", "/Deck/4"],
    ["ArrowLeft", "/Deck/2"],
    ["ArrowUp", "/Deck/2"],
    ["PageUp", "/Deck/2"],
    ["Home", "/Deck/1"],
    ["End", "/Deck/9"],
    ["Escape", "/Deck"],
  ])("maps %s to %s", (key, href) => {
    const { navigate } = renderDeck();
    fireEvent.keyDown(document, { key });
    expect(navigate).toHaveBeenCalledWith(href);
  });

  it("ignores unbound keys", () => {
    const { navigate } = renderDeck();
    fireEvent.keyDown(document, { key: "a" });
    expect(navigate).not.toHaveBeenCalled();
  });

  it("treats a null href as a no-op (Next on the last slide)", () => {
    const { navigate } = renderDeck({ ...deck, nextHref: null });
    fireEvent.keyDown(document, { key: "ArrowRight" });
    expect(navigate).not.toHaveBeenCalled();
    // The other keys still work — only the disabled one is inert.
    fireEvent.keyDown(document, { key: "ArrowLeft" });
    expect(navigate).toHaveBeenCalledWith("/Deck/2");
  });

  it("never hijacks keys while the user is typing in a field", () => {
    const { navigate } = renderDeck();
    const input = document.createElement("input");
    document.body.appendChild(input);
    fireEvent.keyDown(input, { key: "ArrowRight" });
    expect(navigate).not.toHaveBeenCalled();
    document.body.removeChild(input);
  });

  it("unmounting removes the listener (no stacking across Present re-renders)", () => {
    const { navigate, unmount } = renderDeck();
    unmount();
    fireEvent.keyDown(document, { key: "ArrowRight" });
    expect(navigate).not.toHaveBeenCalled();
  });
});
