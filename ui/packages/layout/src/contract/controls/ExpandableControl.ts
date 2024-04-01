import { Control, MessageAndAddress } from "./Control";

export class ExpandableControl extends Control {
    expandMessage?: MessageAndAddressAndArea;
}

export interface MessageAndAddressAndArea extends MessageAndAddress {
    readonly area: string;
}