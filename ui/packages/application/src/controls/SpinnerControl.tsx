import loader from '@open-smc/ui-kit/components/loader.module.scss';
import {ControlView} from "../ControlDef";

export default function SpinnerControl({id}: ControlView) {
    return (
        <div id={id} className={loader.loading}>Loading...</div>
    )
}