// @vitest-environment jsdom
// Nested node-menu entries render as REAL Fluent sub-menus.
//
// The regression these pin: `flattenMenuItems` (the port of Blazor's FlattenMenuItems) used to
// delete the parent and splice its children inline behind a divider. With two GitHub sync sources
// configured that produced two identical "Sync now / Update to latest / Check branch" triplets and
// no way to tell which repo either belonged to — the parent's label, icon and repo tooltip were the
// only thing that distinguished them, and all three were thrown away.
//
// Also pinned here, because a submenu is only usable if these hold:
//   - a grouping parent is NOT activatable (it opens; it never navigates)
//   - the submenu is reachable by KEYBOARD, not hover only
//   - Fluent's menu roles / aria-haspopup / aria-expanded are actually emitted
//   - children come out in `order`, at depth as well as at the top level

import { describe, expect, it, vi, afterEach } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { FluentProvider, Menu, MenuList, MenuPopover, MenuTrigger, webLightTheme, Button } from "@fluentui/react-components";
import { MenuEntries, GROUP_AREA, type MenuItemDef } from "../src/client/HeaderMenus";

afterEach(cleanup);

const leaf = (label: string, order = 0): MenuItemDef => ({ label, area: label, order });

/** The menu open, so its items are in the DOM — the header's MenuButton in miniature. */
function OpenMenu({ items, onItem }: { items: MenuItemDef[]; onItem?: (i: MenuItemDef) => void }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <Menu open>
        <MenuTrigger disableButtonEnhancement>
          <Button>trigger</Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuEntries items={items} onItem={onItem ?? (() => {})} />
          </MenuList>
        </MenuPopover>
      </Menu>
    </FluentProvider>
  );
}

const parentOf = (name: RegExp) => screen.getByRole("menuitem", { name });

const click = async (el: Element) => {
  await act(async () => {
    fireEvent.click(el);
  });
};

const exportGroup: MenuItemDef = {
  label: "Export",
  area: GROUP_AREA,
  icon: "📦",
  tooltip: "Export or share this document",
  order: 27,
  children: [
    { label: "Share as email", area: "SendDocument", icon: "📤", order: 29 },
    { label: "Export to PDF", area: "ExportPdf", icon: "📄", order: 27 },
    { label: "Export to DOCX", area: "ExportDocx", icon: "📝", order: 28 },
  ],
};

describe("MenuEntries — nested items render as sub-menus", () => {
  it("renders the PARENT, not its children spliced inline", () => {
    render(<OpenMenu items={[leaf("Edit"), exportGroup]} />);

    // The parent survives — this is the whole point.
    expect(screen.getByText("Export")).toBeTruthy();
    // Children stay behind the closed submenu until it is opened.
    expect(screen.queryByText("Export to PDF")).toBeNull();
  });

  it("exposes the parent with the right role and aria-haspopup, collapsed", () => {
    render(<OpenMenu items={[exportGroup]} />);

    const parent = parentOf(/Export/);
    expect(parent.getAttribute("aria-haspopup")).toBe("menu");
    expect(parent.getAttribute("aria-expanded")).toBe("false");
  });

  it("opens the submenu from the KEYBOARD — hover is not required", async () => {
    render(<OpenMenu items={[exportGroup]} />);

    const parent = parentOf(/Export/);
    await act(async () => {
      parent.focus();
      fireEvent.keyDown(parent, { key: "ArrowRight", code: "ArrowRight" });
    });

    expect(screen.getByText("Export to PDF")).toBeTruthy();
    expect(parent.getAttribute("aria-expanded")).toBe("true");
  });

  it("orders children by `order`, not by the order they arrived in", async () => {
    render(<OpenMenu items={[exportGroup]} />);

    // Declared email(29) → pdf(27) → docx(28); must render pdf → docx → email.
    await click(parentOf(/Export/));

    const labels = screen
      .getAllByRole("menuitem")
      .map((el) => el.textContent ?? "")
      .filter((t) => t.includes("Export to") || t.includes("as email"));
    expect(labels.map((l) => l.replace(/[^\w ]/g, "").trim())).toEqual([
      "Export to PDF",
      "Export to DOCX",
      "Share as email",
    ]);
  });

  it("never activates a grouping parent — it opens, it does not navigate", async () => {
    const onItem = vi.fn();
    render(<OpenMenu items={[exportGroup]} onItem={onItem} />);

    await click(parentOf(/Export/));

    // The submenu opened…
    expect(screen.getByText("Export to PDF")).toBeTruthy();
    // …and nothing was navigated to.
    expect(onItem).not.toHaveBeenCalled();
  });

  it("still activates a leaf", async () => {
    const onItem = vi.fn();
    render(<OpenMenu items={[leaf("Edit")]} onItem={onItem} />);

    await click(parentOf(/Edit/));

    expect(onItem).toHaveBeenCalledTimes(1);
    expect(onItem.mock.calls[0][0].area).toBe("Edit");
  });

  it("activates a CHILD, passing the child's own definition", async () => {
    const onItem = vi.fn();
    render(<OpenMenu items={[exportGroup]} onItem={onItem} />);

    await click(parentOf(/Export/));
    await click(screen.getByText("Export to PDF"));

    expect(onItem).toHaveBeenCalledTimes(1);
    expect(onItem.mock.calls[0][0].area).toBe("ExportPdf");
  });

  it("nests to arbitrary depth", async () => {
    const deep: MenuItemDef = {
      label: "L1",
      area: GROUP_AREA,
      children: [{ label: "L2", area: GROUP_AREA, children: [leaf("L3")] }],
    };
    render(<OpenMenu items={[deep]} />);

    await click(parentOf(/L1/));
    await click(screen.getByText("L2"));

    expect(screen.getByText("L3")).toBeTruthy();
  });

  it("renders a separator as a divider, never as an entry", () => {
    render(<OpenMenu items={[leaf("Edit"), { label: "", area: "_separator" }, leaf("Delete")]} />);

    const list = screen.getByRole("menu");
    expect(within(list).getAllByRole("menuitem").map((e) => e.textContent)).toEqual(["Edit", "Delete"]);
  });
});
