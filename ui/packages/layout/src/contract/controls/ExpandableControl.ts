import { UiControl, MessageAndAddress } from "./UiControl";

export class ExpandableControl extends UiControl {
    expandMessage?: MessageAndAddressAndArea;
}

export interface MessageAndAddressAndArea extends MessageAndAddress {
    readonly area: string;
}