import {InputText} from "@open-smc/ui-kit/src/components/InputText";
import { useControlContext } from "../ControlContext";
import {useState, useEffect} from 'react';
import { ControlView } from "../ControlDef";

export interface TextboxView extends ControlView {
    data?: string;
}

export default function TextboxControl({id, data}: TextboxView) {
    const {onChange} = useControlContext();
    const [value, setValue] = useState(data);

    useEffect(() => {
        setValue(data);
    }, [data])

    return <InputText id={id} value={value ?? ''}
            onChange={event => {
                onChange("data", event.target.value);
                setValue(event.target.value);
            }
        } />;
}