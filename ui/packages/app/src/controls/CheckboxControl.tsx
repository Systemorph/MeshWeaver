import React, { useState } from "react";
import Checkbox from "rc-checkbox";
import { useControlContext } from "../ControlContext";
import styles from './checkboxControl.module.scss';
import classNames from "classnames";
import { v4 as uuid } from "uuid";
import { ControlView } from "../ControlDef";
import { useAppDispatch } from "../store/hooks";
import { updateStore } from "@open-smc/data/src/updateStoreReducer";

export interface CheckboxView extends ControlView {
    data?: boolean;
}

export default function CheckboxControl({id, data, label, isReadonly}: CheckboxView) {
    const {layoutAreaModel: {area}} = useControlContext();
    const [checkboxId] =  useState(uuid());
    const actualId = id ?? checkboxId;

    const appDispatch = useAppDispatch();

    const labelClassName = classNames(styles.checkboxControl, {[styles.disabled]: isReadonly});

    return (
        <label htmlFor={actualId} className={labelClassName}>
            <Checkbox
                id={actualId}
                disabled={isReadonly}
                checked={data}
                onChange={(event: any) => {
                    appDispatch(
                        updateStore(
                            state => {
                                state.areas[area].props.data = event.target.checked
                            }
                        )
                    )
                }}
            />
            {label && <span>{label}</span>}
        </label>
    );
}
