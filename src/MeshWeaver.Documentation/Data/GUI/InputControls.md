---
Name: Form Input Controls
Category: Documentation
Description: The individual input controls — checkboxes, switches, date pickers, text areas, list selectors, radio groups, and the mesh-node picker — each rendered live.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="6" rx="2"/><rect x="3" y="14" width="18" height="6" rx="2"/><path d="M7 7h.01"/><path d="M7 17h.01"/></svg>
---

# Form Input Controls

Every input control is an immutable record bound to a value. When the [Editor](../Editor) macro
generates a form it *picks these controls for you* based on property types and attributes — but each
control is equally usable on its own inside any container. This page shows each one live.

All input controls derive from `FormControlBase` and share the same fluent surface:

| Method | Purpose |
|---|---|
| `WithLabel(object)` | Field label shown above/beside the input |
| `WithDisabled(object)` | Disable interaction |
| `WithRequired(object)` | Mark as required |
| `WithPlaceholder(object)` | Hint text when empty |

In a real layout area the `Data` argument is usually a `JsonPointerReference` into the area's data
store (see [Data Binding](../DataBinding)); in these standalone demos it is a plain value.

---

# Checkbox and Switch

`CheckBoxControl` binds a boolean to a classic checkbox; `SwitchControl` is the same boolean as a
toggle, with optional state messages.

```csharp --render InputCheckboxSwitch --show-code
Controls.Stack
    .WithView(Controls.CheckBox(true).WithLabel("Send me release notes"))
    .WithView(Controls.CheckBox(false).WithLabel("Enable experimental features"))
    .WithView(Controls.Switch(true)
        .WithLabel("Live updates")
        .WithCheckedMessage("Streaming")
        .WithUncheckedMessage("Paused"))
```

---

# Date and Time

`DateTimeControl` renders a date picker bound to a `DateTime` value. The `Editor` macro selects it
automatically for `DateTime` properties.

```csharp --render InputDateTime --show-code
Controls.Stack
    .WithView(Controls.DateTime(DateTime.Today.AddDays(14))
        .WithLabel("Review deadline"))
    .WithView(Controls.DateTime(new DateTime(2026, 1, 1))
        .WithLabel("Effective from"))
```

---

# Text Area

`TextAreaControl` is the multi-line counterpart of `Controls.Text` — use it for notes, descriptions,
and anything longer than a single line.

```csharp --render InputTextArea --show-code
new TextAreaControl(
        "Ordered 25 units on 2026-06-28.\n" +
        "Awaiting delivery confirmation from the supplier.")
    .WithLabel("Order notes")
    .WithRows(4)
```

---

# List Selection: Select, Combobox, Listbox

Three controls cover single-selection from a list of `Option<T>` values. They share the same
`(data, options)` shape and differ only in presentation:

| Control | Presentation | Reach for it when… |
|---|---|---|
| `Controls.Select` | Closed dropdown | The default — compact, familiar |
| `Controls.Combobox` | Dropdown with free-text filtering | The list is long enough to search |
| `Controls.Listbox` | Always-open list | The choice should stay visible |

```csharp --render InputListControls --show-code
var currencies = new[]
{
    new Option<string>("CHF", "Swiss Franc (CHF)"),
    new Option<string>("EUR", "Euro (EUR)"),
    new Option<string>("USD", "US Dollar (USD)"),
    new Option<string>("GBP", "British Pound (GBP)")
};

Controls.Stack
    .WithView(Controls.Select("CHF", currencies).WithLabel("Reporting currency (Select)"))
    .WithView(Controls.Combobox("EUR", currencies).WithLabel("Trade currency (Combobox)"))
    .WithView(Controls.Listbox("USD", currencies).WithLabel("Settlement currency (Listbox)"))
```

---

# Radio Group

`RadioGroupControl` shows every option at once as radio buttons. The third argument is the value
type name (used to type the selection client-side) — `"String"` for string options.

```csharp --render InputRadioGroup --show-code
var riskProfiles = new[]
{
    new Option<string>("Conservative", "Conservative — capital preservation"),
    new Option<string>("Balanced",     "Balanced — mixed growth and income"),
    new Option<string>("Dynamic",      "Dynamic — long-term growth")
};

Controls.RadioGroup("Balanced", riskProfiles, "String")
    .WithLabel("Investment risk profile")
```

---

# Mesh Node Picker

`MeshNodePickerControl` selects a **mesh node** and stores its PATH. Scope the candidate set with
[query syntax](/Doc/DataMesh/QuerySyntax) — here it offers the pages of this documentation area:

```csharp --render InputNodePicker --show-code
Controls.MeshNodePicker("Doc/GUI/DataGrid")
    .WithQueries("namespace:Doc/GUI scope:descendants nodeType:Markdown")
    .WithMaxResults(10)
    .WithLabel("Pick a documentation page")
```

This is the standard control whenever content references other content — never hand-build a select
over node paths.

---

# Signature Pad

`SignaturePadControl` is the hand-drawn signature of a paper contract: a canvas the viewer signs
on with a finger, a pen or the mouse, over a signature line. The bound value is the drawn
signature as a PNG data URL (`data:image/png;base64,…`), empty while nothing has been drawn.
**Done** pushes the image into the bound stream — never each stroke — and **Clear** empties it.
A value that is already set renders as the stored image, so a signature that was given shows
where it was given. Made for a phone or a tablet as much as for a desktop.

```csharp --render InputSignaturePad --show-code
Controls.Stack
    .WithView(Controls.SignaturePad("")
        .WithLabel("Signature")
        .WithPlaceholder("Sign here")
        .WithWidth(480).WithHeight(160)
        .WithPenColor("#1a237e"))
```

| Method | Purpose | Default |
|---|---|---|
| `WithWidth(int)` / `WithHeight(int)` | Drawing size in CSS pixels | 480 × 160 |
| `WithPenColor(string)` | Pen colour (any CSS colour) | `#1a237e` |
| `WithPlaceholder(object)` | The hint under the signature line | localized *Sign here* |
| `WithClearButton(object)` · `WithClearButton()` | Offer the Clear button (bindable, like `WithPenColor`) | `true` |

The two buttons, the hint and the canvas's accessible name are catalog keys (`signaturePad.*`), so a
German reader is handed *Hier unterschreiben*, *Löschen*, *Fertig*.

---

# Image Canvas

`ImageCanvasControl` is a picture the viewer edits by **dragging on it** — today a crop region with
handles on its edges and corners. It is a thin overlay, never a second image renderer: the control
draws the picture exactly as given (the server has already rendered it) and, on top, only the crop
frame and its handles. A drag is shown locally while it lasts and commits **once, on release**, as
ordinary field edits of the four bound trims; the server re-renders and the new picture flows back.

The trims are in **model units** — the picture's full extent is `ModelWidth × ModelHeight`
(typically the source image's pixels), whatever size it is drawn at — and each is the amount
removed from that edge. Before writing, a renderer applies `ImageCanvasCrop.Normalize`: trims are
clamped so at least one unit remains, and the edge being dragged stops at the opposite one instead
of pushing it. A corner moves two edges at once and passes both
(`Normalize(ImageCanvasEdge.Top, ImageCanvasEdge.Left)`).

```csharp --render InputImageCanvas --show-code
Controls.Stack
    .WithView(Controls.ImageCanvas(
            "data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='400' height='240'><rect width='400' height='240' fill='%234f46e5'/><circle cx='200' cy='120' r='70' fill='%23facc15'/></svg>",
            "A yellow circle on a blue background")
        .WithModelSize(400, 240)
        .WithCrop(40, 20, 40, 20))
```

| Method | Purpose | Default |
|---|---|---|
| `WithModelSize(width, height)` | The picture's full extent in model units — required for the crop tool | — |
| `WithCrop(left, top, right, bottom)` | Bind the crop tool to the four trims (typically `JsonPointerReference`s into the node) | no crop tool |
| `WithMaxHeight(object)` | The tallest the picture is drawn (CSS length); its width never exceeds the container | `70vh` |
| `WithAlt(object)` | The picture's accessible name | — |

The accessible names of the crop frame and its eight handles are catalog keys (`imageCanvas.*`), so
a German reader hears *Linke Kante des Zuschnitts*. Every value the canvas edits should also be
editable as a number in a form beside it, so keyboard and screen-reader users lose nothing.

---

# See Also

- [Editor](../Editor) — auto-generates a form from a record using these controls
- [Data Binding](../DataBinding) — how `Data` binds two-way through JSON pointers
- [Attributes](../Attributes) — `[UiControl<T>]` and validation annotations that drive control choice
