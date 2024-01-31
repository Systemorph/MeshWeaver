import * as RcSelect from "rc-select/lib/Select";
import {useMemo} from "react";
import {get, isFunction, map} from "lodash";

// TODO: add less support and use rc-select's styles directly from node_modules (10/28/2022, akravets)
// rc-select.scss is a compiled version of rc-select's less styles
import "./rc-select.css";
import "./select.scss";

export interface SelectProps<TValue> extends Omit<RcSelect.SelectProps<RcSelect.DefaultOptionType>, 'value' | 'options' | 'onSelect'> {
    value?: TValue;
    options: TValue[];
    keyBinding?: string | ((value: TValue) => string);
    nameBinding?: string | ((value: TValue) => string);
    onSelect?: (value: TValue) => void;
}

interface SelectOption<TValue> extends RcSelect.DefaultOptionType {
    data: TValue;
}

export function Select<TValue>({
                                   value,
                                   options,
                                   keyBinding,
                                   nameBinding,
                                   onSelect,
                                   ...selectProps
                               }: SelectProps<TValue>) {
    const mapValueToOption = (value: TValue) => ({
        value: keyBinding ?
            (isFunction(keyBinding) ? keyBinding(value) : get(value, keyBinding)) : value as any,
        label: nameBinding ?
            (isFunction(nameBinding) ? nameBinding(value) : get(value, nameBinding)) : value,
        data: value
    }) as SelectOption<TValue>;

    const mappedValue = useMemo(() => mapValueToOption(value), [value]);
    const mappedOptions = useMemo(() => map(options, mapValueToOption), [options]);

    return (
        <RcSelect.default
            inputIcon={<i className={"sm sm-chevron-down"}/>}
            {...selectProps}
            value={mappedValue}
            options={mappedOptions}
            onSelect={(value: unknown, option: SelectOption<TValue>) => {
                onSelect(option.data);
            }}
        />
    );
}