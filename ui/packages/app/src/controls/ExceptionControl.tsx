import { ControlView } from "../ControlDef";

export interface ExceptionView extends ControlView {
    type: string;
    message: string;
}

export default function ExceptionControl({id, message, type}: ExceptionView) {
    return <span id={id} style={{color: 'red'}}>{message}</span>;
}