import { Style } from "./Style";
import { Bindable } from "../Binding";
import { DataInput } from "@open-smc/data/src/contract/DataInput";

export class Control {
    id?: Bindable<string>;
    isReadonly?: Bindable<boolean>;
    clickMessage?: Bindable<MessageAndAddress>;
    style?: Bindable<Style>;
    className?: Bindable<string>;
    skin?: Bindable<string>;
    tooltip?: Bindable<string>;
    data?: Bindable<unknown>;
    label?: Bindable<string>;
    dataContext?: DataInput;
}

export interface MessageAndAddress {
    message: unknown;
    address: unknown;
}