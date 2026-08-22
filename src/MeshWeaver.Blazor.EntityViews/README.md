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

Plus the shared form-component bases (`FormComponentBase`, `InputBase`, `ListBase`) and the
`OptionsExtension.Option` list-item model — `MeshWeaver.Blazor.Graph`'s `MeshNodePickerView`
derives from `FormComponentBase`, which is why that pack references this one.

## Registration

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
