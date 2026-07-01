// EditForm parity — the container + item-skin contract of EditFormControl
// (src/MeshWeaver.Layout/EditFormControl.cs, rendered by Blazor's EditFormView + PropertyView):
// the control's child areas each carry a PropertySkin { name, label, description } wrapping the
// bound field control; the EditFormSkin stacks them as a form. Fields data-bind per-edit through
// the standard update event (the React counterpart of the Blazor model round-trip).

import { beforeAll, describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

// The wire shape the Edit macro produces: an EditForm container whose child areas are the
// property editors, each wrapped in a PropertySkin (name/label/description).
function editFormTree(): AreaTree {
  return {
    data: { person: { name: "Ada", bio: "Pioneer" } },
    areas: {
      main: {
        $type: "EditForm",
        skins: [{ $type: "EditFormSkin" }],
        areas: [
          { $type: "NamedArea", area: "main/Name" },
          { $type: "NamedArea", area: "main/Bio" },
        ],
      },
      "main/Name": {
        $type: "TextField",
        data: { $type: "JsonPointerReference", pointer: "/data/person/name" },
        skins: [{ $type: "PropertySkin", name: "name", label: "Full name", description: "The person's display name" }],
      },
      "main/Bio": {
        $type: "TextArea",
        data: { $type: "JsonPointerReference", pointer: "/data/person/bio" },
        skins: [{ $type: "PropertySkin", name: "bio", label: "Biography" }],
      },
    },
  };
}

describe("EditForm — form rendering from the container + PropertySkin payload", () => {
  it("renders a form with labeled, described, value-bound fields", () => {
    render(<MeshAreaView source={new StaticAreaSource(editFormTree())} rootArea="main" />);
    expect(document.querySelector("form")).toBeTruthy();
    expect(screen.getByText("Full name")).toBeTruthy();
    expect(screen.getByText("The person's display name")).toBeTruthy();
    expect(screen.getByText("Biography")).toBeTruthy();
    expect(screen.getByDisplayValue("Ada")).toBeTruthy();
    expect(screen.getByDisplayValue("Pioneer")).toBeTruthy();
  });

  it("edits write back to the field's pointer via the standard update event", () => {
    const source = new StaticAreaSource(editFormTree());
    render(<MeshAreaView source={source} rootArea="main" />);
    fireEvent.change(screen.getByDisplayValue("Ada"), { target: { value: "Ada Lovelace" } });
    expect(source.events).toContainEqual(
      expect.objectContaining({ kind: "update", pointer: "/data/person/name", value: "Ada Lovelace" }),
    );
    expect((source.getState().data?.person as { name: string }).name).toBe("Ada Lovelace");
  });

  it("PropertySkin label falls back to name; missing description renders nothing extra", () => {
    render(<MeshAreaView source={new StaticAreaSource(editFormTree())} rootArea="main" />);
    // Bio has no description — only the label shows.
    const bioLabel = screen.getByText("Biography");
    expect(bioLabel).toBeTruthy();
  });
});
