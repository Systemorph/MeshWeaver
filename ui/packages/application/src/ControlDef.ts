import { Style } from "./contract/controls/Style";
import { MessageAndAddress } from "./contract/controls/Control";

export interface ControlView {
    readonly id?: string;
    readonly isReadonly?: boolean;
    readonly clickMessage?: MessageAndAddress;
    readonly style?: Style;
    readonly className?: string;
    readonly skin?: string;
    readonly tooltip?: string;
    readonly data?: unknown;
    readonly label?: string;
}

export interface ExpandableView extends ControlView {
    readonly expandMessage?: MessageAndAddressAndArea;
}

export interface MessageAndAddressAndArea extends MessageAndAddress {
    readonly area: string;
}

export type ControlDef<TView extends ControlView = unknown> = TView & {
    readonly $type: string;
    readonly moduleName: string;
    readonly apiVersion: string;
    readonly address?: unknown;
    readonly dataContext?: unknown;
}