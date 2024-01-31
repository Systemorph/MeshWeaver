import React, { useState, useEffect } from "react";
import Checkbox from "rc-checkbox";
import { useControlContext } from "../ControlContext";
import styles from './checkboxControl.module.scss';
import classNames from "classnames";
import { v4 as uuid } from "uuid";
import { ControlView } from "../ControlDef";

export interface CheckboxView extends ControlView {
    data?: boolean;
}

export default function CheckboxControl({id, data, label, isReadonly}: CheckboxView) {
    const {onChange} = useControlContext();
    const [value, setValue] = useState(data);
    const [checkboxId] =  useState(uuid());
    const actualId = id ?? checkboxId;

    useEffect(() => {
        setValue(data);
    }, [data]);

    const onChangeHandler = (e: any) => {
        onChange("data", e.target.checked);
        setValue(e.target.checked);
    };

    const labelClassName = classNames(styles.checkboxControl, {[styles.disabled]: isReadonly});

    return (
        <label htmlFor={actualId} className={labelClassName}>
            <Checkbox id={actualId} disabled={isReadonly} checked={value} onChange={onChangeHandler}/>
            {label && <span>{label}</span>}
        </label>
    );
}
