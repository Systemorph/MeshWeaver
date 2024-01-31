import {InputNumber} from "@open-smc/ui-kit/components/InputNumber";
import { useControlContext } from "../ControlContext";
import { useState, useEffect } from 'react';
import { ControlView } from "../ControlDef";

export interface NumberView extends ControlView {
    data?: number;
}

export default function NumberControl({id,data}: NumberView) {
    const {onChange} = useControlContext();
    const [value, setValue] = useState(data);

    useEffect(() => {
        setValue(data);
    }, [data])

    return <InputNumber id={id} value={value ?? ''}
            onChange={(value) => {
                onChange("data", value);
                setValue(value as number);
            }} />;
}