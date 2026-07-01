# React Native / Expo — the MAUI peer

The renderer core is leaf-pack-swappable: `ControlRenderer` + `area/*` import **no** concrete
component — they pull the active leaf pack from a `RegistryProvider`. The web entry installs a Fluent
DOM pack; a React Native app imports the Fluent-free **`@meshweaver/react/core`** entry and supplies an
RN `<View>`/`<Text>` pack. Same `UiControl` tree the Blazor portal and MAUI render — a native leaf pack
instead of the web one. This is the direct analog of **MAUI's `MauiViewPack`** (native) vs Blazor's web
renderers; here it's an RN pack vs the Fluent DOM pack.

```
            @meshweaver/react/core   (dispatch • binding • skins • area stream — NO DOM/Fluent)
                       │
          ┌────────────┴────────────┐
   Fluent DOM pack            RN pack (below)
   @meshweaver/react       <View>/<Text>/<TextInput>
   (web, Electron)         (iOS, Android — the MAUI peer)
```

## A starter RN leaf pack

A `LeafPack` is `{ controls, skins, fallback, defaultContainer }`. The shape is identical to the Fluent
pack — only the leaf components change. This covers the common controls; grow it the same way the Fluent
pack does. (Requires `react-native`; this is reference code, not built into the npm package.)

```tsx
// rnPack.tsx
import { View, Text, TextInput, Pressable, Switch, ScrollView } from "react-native";
import type { LeafPack, SkinComponent, ControlComponent } from "@meshweaver/react/core";
import { useResolve, useEmit, useScope, useChildAreas, RenderArea, ControlRenderer } from "@meshweaver/react/core";

const Children = ({ control }: any) =>
  useChildAreas(control).map((c, i) => <RenderArea key={c.key || i} areaKey={c.key} />);

const stack: SkinComponent = ({ skin, control }) => (
  <View style={{ flexDirection: String(skin.orientation).toLowerCase() === "horizontal" ? "row" : "column", gap: (skin.verticalGap ?? skin.horizontalGap ?? 8) as number }}>
    <Children control={control} />
  </View>
);

const card: SkinComponent = ({ control }) => (
  <View style={{ padding: 12, borderRadius: 8, borderWidth: 1, borderColor: "#e1e1e1", gap: 8 }}>
    <ControlRenderer control={control} />
  </View>
);

const Label: ControlComponent = ({ control }) => <Text>{String(useResolve(control.data) ?? "")}</Text>;

const Button: ControlComponent = ({ control }) => {
  const emit = useEmit(); const { area } = useScope();
  return (
    <Pressable onPress={() => control.isClickable && emit({ kind: "click", area })} style={{ backgroundColor: "#0f6cbd", padding: 10, borderRadius: 6 }}>
      <Text style={{ color: "white", textAlign: "center" }}>{String(useResolve(control.data) ?? "")}</Text>
    </Pressable>
  );
};

const TextField: ControlComponent = ({ control }) => {
  const value = String(useResolve(control.data) ?? "");
  const emit = useEmit(); const { area } = useScope();
  // (use useBindingPointer(control.data) for the write-back pointer, as the Fluent pack does)
  return <TextInput value={value} style={{ borderWidth: 1, borderColor: "#ccc", borderRadius: 4, padding: 8 }}
    onChangeText={(t) => emit({ kind: "update", area, pointer: bindingPointerOf(control), value: t })} />;
};

const fallback: ControlComponent = ({ control }) => <Text style={{ color: "red" }}>Unsupported: {control.$type}</Text>;

export const rnPack: LeafPack = {
  defaultContainer: stack,
  skins: { LayoutStack: stack, Card: card, __default: ({ control }) => <ControlRenderer control={control} /> },
  controls: { Label, Markdown: Label, Badge: Label, Button, TextField, /* …add the rest… */ },
  fallback,
};
```

## Wire it in an Expo app

```tsx
// App.tsx
import { SafeAreaView, ScrollView } from "react-native";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";
import { sampleArea } from "./sample"; // or a GrpcAreaSource for live mesh data

const source = new StaticAreaSource(sampleArea);

export default function App() {
  return (
    <SafeAreaView style={{ flex: 1 }}>
      <ScrollView contentContainerStyle={{ padding: 16 }}>
        <RegistryProvider pack={rnPack}>
          <ScopeProvider source={source} area="main">
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </RegistryProvider>
      </ScrollView>
    </SafeAreaView>
  );
}
```

## Setup

```bash
npx create-expo-app meshweaver-mobile && cd meshweaver-mobile
npm install @meshweaver/react @meshweaver/client   # core + (optional) live transport
# drop in rnPack.tsx + App.tsx above; reuse the demo's sample.ts
npx expo start                                      # press i for the iOS simulator
```

For **live** mesh data, swap `StaticAreaSource` for `GrpcAreaSource` (from `@meshweaver/react/core`),
exactly as on web — the renderer, binding, and event plumbing are identical across web, Electron, and
native, because they all live in the Fluent-free core.
