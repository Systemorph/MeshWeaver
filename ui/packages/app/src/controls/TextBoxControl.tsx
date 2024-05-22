import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import { ControlView } from "../ControlDef";
import { useAppDispatch } from "../store/hooks";
import { useControlContext } from "../ControlContext";
import { updateStore } from "@open-smc/data/src/updateStoreReducer";
import { AppState } from "../store/appStore";

export interface TextBoxView extends ControlView {
    data?: string;
    placeholder?: string;
}

export default function TextBoxControl({data, placeholder}: TextBoxView) {
    const {layoutAreaModel: {area}} = useControlContext();
    const appDispatch = useAppDispatch();

    return (
        <InputText
            value={data ?? ''}
            placeholder={placeholder}
            onChange={
                event => {
                    appDispatch(
                        updateStore<AppState>(
                            state => {
                                state.areas[area].props.data = event.target.value
                            }
                        )
                    );
                }
            }/>
    );
}