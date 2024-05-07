import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import { ControlView } from "../ControlDef";
import { useAppDispatch } from "../store/hooks";
import { updateAreaActionCreator } from "../store/appReducer";
import { useControlContext } from "../ControlContext";

export interface TextBoxView extends ControlView {
    data?: string;
}

export default function TextBoxControl({data}: TextBoxView) {
    const {layoutAreaModel: {area}} = useControlContext();
    const appDispatch = useAppDispatch();

    return (
        <InputText
            value={data ?? ''}
            onChange={
                event => {
                    appDispatch(
                        updateAreaActionCreator({
                            area,
                            props: {
                                data: event.target.value
                            }
                        })
                    );
                }
            }/>
    );
}