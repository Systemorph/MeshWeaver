import type { ReactNode } from "react";
import {
  Button,
  Checkbox,
  Combobox,
  Dropdown,
  Field,
  Input,
  Listbox,
  MenuItem,
  Option,
  Radio,
  RadioGroup,
  Slider,
  SpinButton,
  Switch,
  Textarea,
} from "@fluentui/react-components";
import { Search20Regular } from "@fluentui/react-icons";
import type { UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshLink } from "../area/navigation.js";
import { str, useClick, useField, useOptions, useText } from "./common.js";
import { MeshIcon } from "./MeshIcon.js";
import { classifyIcon } from "./iconValue.js";
import { controlStyle } from "../render/style.js";

function field(control: UiControl, node: ReactNode): ReactNode {
  const label = useText(control.label);
  return label ? <Field label={label}>{node}</Field> : <>{node}</>;
}

function TextField({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  const password = !!useResolve(control.password);
  return field(
    control,
    <Input
      value={str(f.value)}
      type={password ? "password" : "text"}
      disabled={f.disabled}
      placeholder={f.placeholder}
      contentBefore={iconOf(control.iconStart)}
      contentAfter={iconOf(control.iconEnd)}
      onChange={(_, d) => f.setValue(d.value)}
      onBlur={f.onBlur}
    />,
  );
}

function TextAreaView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return field(
    control,
    <Textarea value={str(f.value)} disabled={f.disabled} placeholder={f.placeholder} onChange={(_, d) => f.setValue(d.value)} onBlur={f.onBlur} />,
  );
}

function NumberField({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  const n = f.value == null || f.value === "" ? undefined : Number(f.value);
  return field(
    control,
    <SpinButton
      value={n ?? null}
      disabled={f.disabled}
      onChange={(_, d) => f.setValue(d.value ?? (d.displayValue != null ? Number(d.displayValue) : undefined))}
      onBlur={f.onBlur}
    />,
  );
}

function CheckBoxView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return <Checkbox checked={!!f.value} disabled={f.disabled} label={f.label || undefined} onChange={(_, d) => f.setValue(!!d.checked)} />;
}

function SwitchView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return <Switch checked={!!f.value} disabled={f.disabled} label={f.label || undefined} onChange={(_, d) => f.setValue(!!d.checked)} />;
}

function SliderView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return (
    <Slider
      min={Number(useResolve(control.min) ?? 0)}
      max={Number(useResolve(control.max) ?? 100)}
      step={Number(useResolve(control.step) ?? 1)}
      value={Number(f.value ?? 0)}
      disabled={f.disabled}
      onChange={(_, d) => f.setValue(d.value)}
    />
  );
}

function DateView({ control, type }: { control: UiControl; type: "date" | "datetime-local" }): ReactNode {
  const f = useField(control);
  const v = f.value ? String(f.value).slice(0, type === "date" ? 10 : 16) : "";
  return field(control, <Input type={type} value={v} disabled={f.disabled} onChange={(_, d) => f.setValue(d.value)} onBlur={f.onBlur} />);
}

function selectIndex(options: { value: unknown }[], value: unknown): number {
  return options.findIndex((o) => str(o.value) === str(value));
}

function SelectView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  const options = useOptions(control);
  const idx = selectIndex(options, f.value);
  return field(
    control,
    <Dropdown
      disabled={f.disabled}
      value={idx >= 0 ? options[idx].text : ""}
      selectedOptions={idx >= 0 ? [String(idx)] : []}
      onOptionSelect={(_, d) => d.optionValue != null && f.setValue(options[Number(d.optionValue)]?.value)}
    >
      {options.map((o, i) => (
        <Option key={i} value={String(i)}>
          {o.text}
        </Option>
      ))}
    </Dropdown>,
  );
}

function ComboboxView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  const options = useOptions(control);
  const idx = selectIndex(options, f.value);
  return field(
    control,
    <Combobox
      disabled={f.disabled}
      freeform
      value={idx >= 0 ? options[idx].text : str(f.value)}
      selectedOptions={idx >= 0 ? [String(idx)] : []}
      onOptionSelect={(_, d) => d.optionValue != null && f.setValue(options[Number(d.optionValue)]?.value)}
      onChange={(e) => f.setValue((e.target as HTMLInputElement).value)}
    >
      {options.map((o, i) => (
        <Option key={i} value={String(i)}>
          {o.text}
        </Option>
      ))}
    </Combobox>,
  );
}

function ListboxView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  const options = useOptions(control);
  const idx = selectIndex(options, f.value);
  return field(
    control,
    <Listbox selectedOptions={idx >= 0 ? [String(idx)] : []} onOptionSelect={(_, d) => d.optionValue != null && f.setValue(options[Number(d.optionValue)]?.value)}>
      {options.map((o, i) => (
        <Option key={i} value={String(i)}>
          {o.text}
        </Option>
      ))}
    </Listbox>,
  );
}

function RadioGroupView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  const options = useOptions(control);
  return field(
    control,
    <RadioGroup value={str(f.value)} disabled={f.disabled} onChange={(_, d) => {
      const o = options.find((x) => str(x.value) === d.value);
      f.setValue(o ? o.value : d.value);
    }}>
      {options.map((o, i) => (
        <Radio key={i} value={str(o.value)} label={o.text} />
      ))}
    </RadioGroup>,
  );
}

function ButtonView({ control }: { control: UiControl }): ReactNode {
  const emitClick = useClick(control);
  const label = useText(control.data) || useText(control.label);
  // Blazor parity (ButtonView.razor): NavigateToHref navigates client-side FIRST, then the
  // ClickedEvent still posts so a server-side ClickAction runs too.
  const link = useMeshLink(useText(control.navigateToHref) || undefined);
  // Honour the control's inline style (WithStyle) — e.g. the pinned-card unpin toggle sizes itself to
  // a small circular icon button (min-width/width/height/border-radius). Without it the icon-only
  // button renders at default width and reads as a bar. Blazor applies Style to the button the same way.
  return (
    <Button
      appearance={(useResolve(control.appearance) as any) ?? "secondary"}
      disabled={!!useResolve(control.disabled)}
      icon={iconOf(control.iconStart)}
      style={controlStyle(control)}
      onClick={(e: React.MouseEvent) => {
        link.onClick?.(e);
        emitClick?.();
      }}
      {...(link.href ? { as: "a", href: link.href } : {})}
    >
      {label}
    </Button>
  );
}

function MenuItemView({ control }: { control: UiControl }): ReactNode {
  const onClick = useClick(control);
  return (
    <MenuItem icon={iconOf(control.icon)} onClick={onClick}>
      {useText(control.title)}
    </MenuItem>
  );
}

function SearchBoxView({ control }: { control: UiControl }): ReactNode {
  const f = useField(control);
  return <Input contentBefore={<Search20Regular />} placeholder={f.placeholder || "Search"} value={str(f.value)} onChange={(_, d) => f.setValue(d.value)} />;
}

function iconOf(value: unknown): JSX.Element | undefined {
  // MeshIcon dispatches every icon shape (name / {provider,id} object / URL / inline SVG / emoji) —
  // menu items and buttons carry emoji and SVG-URL icons in the mesh, not just Fluent names.
  if (classifyIcon(value as never).kind === "none") return undefined;
  return <MeshIcon value={value as never} size={20} />;
}

export const inputControls = {
  TextField,
  TextArea: TextAreaView,
  NumberField,
  CheckBox: CheckBoxView,
  Switch: SwitchView,
  Slider: SliderView,
  Date: (p: { control: UiControl }) => DateView({ ...p, type: "date" }),
  DateTime: (p: { control: UiControl }) => DateView({ ...p, type: "datetime-local" }),
  Select: SelectView,
  Combobox: ComboboxView,
  Listbox: ListboxView,
  RadioGroup: RadioGroupView,
  Button: ButtonView,
  MenuItem: MenuItemView,
  SearchBox: SearchBoxView,
};
