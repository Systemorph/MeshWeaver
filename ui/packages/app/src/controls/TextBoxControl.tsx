import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import { ControlView } from "../ControlDef";
import { LayoutAreaModel } from "../store/appStore";
import { useAppDispatch } from "../store/hooks";
import { setPropsActionCreator } from "../store/appReducer";

export interface TextBoxView extends ControlView {
    data?: string;
}

export default function TextBoxControl({area, props: {data}}: LayoutAreaModel<TextBoxView>) {
    const appDispatch = useAppDispatch();

    return (
        <InputText
            value={data ?? ''}
            onChange={
                event => {
                    appDispatch(
                        setPropsActionCreator({
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