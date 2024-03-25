import { Style } from "./Style";

import { DataInput } from "@open-smc/data/src/data.contract";

export class Control {
    $type: string;
    id?: string;
    isReadonly?: boolean;
    clickMessage?: MessageAndAddress;
    style?: Style;
    className?: string;
    skin?: string;
    tooltip?: string;
    data?: unknown;
    label?: string;
    address?: unknown;
    dataContext?: DataInput<unknown>;
}

export interface MessageAndAddress {
    message: unknown;
    address: unknown;
}