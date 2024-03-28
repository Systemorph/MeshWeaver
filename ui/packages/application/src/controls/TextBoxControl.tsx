import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import { ControlView } from "../ControlDef";
import { ControlProps } from "../app/RenderArea";

export interface TextBoxView extends ControlView {
    data?: string;
}

export default function TextBoxControl({data, onChange}: TextBoxView & ControlProps) {
    return (
        <InputText
            value={data ?? ''}
            onChange={
                event => {
                    onChange("data", event.target.value);
                }
            }/>
    );
}