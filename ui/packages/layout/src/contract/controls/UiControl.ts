import { Style } from "./Style";
import { ValueOrBinding } from "@open-smc/data/src/contract/Binding";
import { type } from "@open-smc/serialization/src/type";
import { ValueOrReference } from "@open-smc/data/src/contract/ValueOrReference";
import {serialize} from "@open-smc/serialization/src/serialize";

@type("OpenSmc.Layout.UiControl")
export class UiControl<T = unknown> {
    id?: ValueOrBinding<string>;
    isReadonly?: ValueOrBinding<boolean>;
    clickMessage?: ValueOrBinding<MessageAndAddress>;
    style?: ValueOrBinding<Style>;
    className?: ValueOrBinding<string>;
    skin?: ValueOrBinding<string>;
    tooltip?: ValueOrBinding<string>;
    data?: ValueOrBinding<unknown>;
    label?: ValueOrBinding<string>;
    dataContext?: ValueOrReference;

    with(config: Partial<T>) {
        Object.assign(this, config);
        return this;
    }

    toJSON() {
        return serialize(this);
    }
}

export interface MessageAndAddress {
    message: unknown;
    address: unknown;
}