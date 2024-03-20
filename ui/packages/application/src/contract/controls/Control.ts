import { Style } from "./Style";

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
    dataContext?: unknown;
}

export interface MessageAndAddress {
    message: unknown;
    address: unknown;
}