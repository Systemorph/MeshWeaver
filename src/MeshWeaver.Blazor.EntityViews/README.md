# MeshWeaver.Blazor.EntityViews

The entity default-view pack: Blazor renderers for the standard entity form/edit controls,
carved out of the base `MeshWeaver.Blazor` pack onto the view-pack seam (the
`MeshWeaver.Blazor.Analysis` precedent).

## What it renders

| Control | View |
|---|---|
| `TextFieldControl` | `TextFieldView` |
| `TextAreaControl` | `TextAreaView` |
| `NumberFieldControl` | `NumberFieldView<T>` (reflection-closed on the control's value type) |
| `DateTimeControl` | `DateTimeView` |
| `RadioGroupControl` | `RadioGroupView<T>` (reflection-closed) |
| `ComboboxControl` | `Combobox` |
| `ListboxControl` | `Listbox` |
| `SelectControl` | `SelectView` |
| `CheckBoxControl` | `Checkbox` |
| `SwitchControl` | `Switch` |
| `EditorSkin` | `EditorView` |
| `EditFormSkin` | `EditFormView` |
| `PropertySkin` | `PropertyView` |

The shared form-component bases (`FormComponentBase`, `InputBase`, `ListBase`) and the
`OptionsExtension.Option` list-item model live in the BASE pack (`MeshWeaver.Blazor.Components`),
not here: they are app-closure infrastructure shared with `MeshWeaver.Blazor.Graph`'s
`MeshNodePickerView`, and an edge from the app-closure Graph pack into this module would drag
this DLL back into the closure — where the module landing refuses the same-identity module.

## Registration

This pack ships as a MODULE (a registry bundle, the Analysis/Radzen/GoogleMaps lane):
`EntityViewsViewPackModuleAttribute` applies the registration when the DLL is listed under
`Modules:Assemblies`, and the portals declare it under `Modules:Required` so a rollout that lost
the pack stalls on readiness instead of shipping blank edit forms. Compiled-in hosts (tests) call
it directly:

```csharp
configuration.AddEntityViews();
```

One `ViewMap`, same dispatch shape as the base registry: pop the skin first, typed arms match
only skin-free controls (see the remarks on `EntityViewsExtensions.EntityViewsMap` for why).
Registration order relative to `AddBlazor()` is not load-bearing: the base registry declines
unknown controls AND unknown skins, and the escaped-HTML fallback lives in its own last-resort
slot.

The control records and skins stay in `MeshWeaver.Layout` — this pack is renderers only.
`ViewPackRegistrationGateTest` (test/MeshWeaver.Hosting.Blazor.Test) gates that every control
above actually resolves to this pack's view through `LayoutClientConfiguration`.
