import { Style } from "@open-smc/layout/src/contract/controls/Style";
import { MessageAndAddress } from "@open-smc/layout/src/contract/controls/UiControl";
import { MessageAndAddressAndArea } from "@open-smc/layout/src/contract/controls/ExpandableControl";

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