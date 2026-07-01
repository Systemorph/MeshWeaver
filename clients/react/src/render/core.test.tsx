import { describe, it, expect } from "vitest";
import { render, screen, act } from "@testing-library/react";
import {
  ControlRenderer,
  RegistryProvider,
  RenderArea,
  ScopeProvider,
  StaticAreaSource,
  useChildAreas,
  useResolve,
  type AreaTree,
  type LeafPack,
} from "../core.js";

// Minimal DOM leaf pack — proves the platform-agnostic core (dispatch, skin popping, binding, child
// area resolution, optimistic updates) with NO Fluent. The same core drives the Fluent web pack and
// an RN pack; this test pins the core itself.
const Children = ({ control }: any) => (
  <>
    {useChildAreas(control).map((c, i) => (
      <RenderArea key={i} areaKey={c.key} />
    ))}
  </>
);

const stack = ({ control }: any) => (
  <div data-testid="stack">
    <Children control={control} />
  </div>
);

const testPack: LeafPack = {
  defaultContainer: stack,
  skins: {
    LayoutStack: stack,
    Card: ({ control }: any) => (
      <div data-testid="card">
        <ControlRenderer control={control} />
      </div>
    ),
    __default: ({ control }: any) => <ControlRenderer control={control} />,
  },
  controls: {
    Label: ({ control }: any) => <span>{String(useResolve(control.data) ?? "")}</span>,
  },
  fallback: ({ control }: any) => <span>unsupported:{control.$type}</span>,
};

function mount(tree: AreaTree, root = "main") {
  const source = new StaticAreaSource(tree);
  const ui = (
    <RegistryProvider pack={testPack}>
      <ScopeProvider source={source} area={root}>
        <RenderArea areaKey={root} />
      </ScopeProvider>
    </RegistryProvider>
  );
  return { source, ...render(ui) };
}

const tree: AreaTree = {
  data: { who: "Ada" },
  areas: {
    main: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack" }, { $type: "Card" }],
      areas: [
        { $type: "NamedArea", area: "title" },
        { $type: "NamedArea", area: "bound" },
      ],
    },
    title: { $type: "Label", data: "Hello" },
    bound: { $type: "Label", data: { $type: "JsonPointerReference", pointer: "/data/who" } },
  },
};

describe("renderer core", () => {
  it("dispatches on $type, pops skins, resolves bindings + child areas", () => {
    mount(tree);
    expect(screen.getByTestId("card")).toBeTruthy(); // outer skin (popped last) wraps
    expect(screen.getByTestId("stack")).toBeTruthy(); // inner layout skin
    expect(screen.getByText("Hello")).toBeTruthy(); // literal
    expect(screen.getByText("Ada")).toBeTruthy(); // bound to /data/who
  });

  it("re-renders bound controls on an optimistic update", () => {
    const { source } = mount(tree);
    act(() => source.emit({ kind: "update", area: "bound", pointer: "/data/who", value: "Grace" }));
    expect(screen.getByText("Grace")).toBeTruthy();
    expect(screen.queryByText("Ada")).toBeNull();
  });

  it("renders the fallback for an unknown control type", () => {
    mount({ areas: { main: { $type: "Nonsense" } } });
    expect(screen.getByText("unsupported:Nonsense")).toBeTruthy();
  });
});
