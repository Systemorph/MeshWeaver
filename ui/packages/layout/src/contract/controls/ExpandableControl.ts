import { UiControl, MessageAndAddress } from "./UiControl";

export class ExpandableControl<T = unknown> extends UiControl<T> {
    expandMessage?: MessageAndAddressAndArea;
}

export interface MessageAndAddressAndArea extends MessageAndAddress {
    readonly area: string;
}