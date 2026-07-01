import { useBindingPointer, useEmit, useResolve, useScope } from "../area/context.js";
import type { Json, UiControl } from "../area/types.js";

export function str(v: Json): string {
  return v == null ? "" : String(v);
}

export function useText(value: Json): string {
  return str(useResolve(value));
}

/** Click handler that posts a ClickedEvent for the current area (only when the control is clickable). */
export function useClick(control: UiControl): (() => void) | undefined {
  const emit = useEmit();
  const { area } = useScope();
  return control.isClickable ? () => emit({ kind: "click", area }) : undefined;
}

export interface Field {
  value: Json;
  setValue: (v: Json) => void;
  onBlur: () => void;
  disabled: boolean;
  required: boolean;
  label: string;
  placeholder: string;
}

/** Bound form field: resolves the value, and writes edits back via UpdatePointer to its /data pointer. */
export function useField(control: UiControl): Field {
  const bound = control.data ?? control.isChecked;
  const value = useResolve(bound);
  const pointer = useBindingPointer(bound);
  const disabled = !!useResolve(control.disabled);
  const required = !!useResolve(control.required);
  const label = str(useResolve(control.label));
  const placeholder = str(useResolve(control.placeholder));
  const emit = useEmit();
  const { area } = useScope();
  return {
    value,
    setValue: (v: Json) => {
      if (pointer) emit({ kind: "update", area, pointer, value: v });
    },
    onBlur: () => emit({ kind: "blur", area }),
    disabled,
    required,
    label,
    placeholder,
  };
}

/** Resolve the `options` of a list control to [{ value, text }]. Accepts Option records or scalars. */
export function useOptions(control: UiControl): { value: Json; text: string }[] {
  const opts = useResolve(control.options);
  if (!Array.isArray(opts)) return [];
  return opts.map((o) => {
    if (o != null && typeof o === "object") {
      const value = "item" in o ? o.item : "value" in o ? o.value : o.text;
      return { value, text: str(o.text ?? value) };
    }
    return { value: o, text: str(o) };
  });
}
