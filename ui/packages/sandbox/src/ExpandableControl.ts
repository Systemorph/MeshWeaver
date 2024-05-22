import { ExpandableView, MessageAndAddressAndArea } from "@open-smc/application/src/ControlDef";
import { ControlBase } from "./ControlBase";

export abstract class ExpandableControl extends ControlBase implements ExpandableView {
    expandMessage: MessageAndAddressAndArea;

    protected constructor($type: string) {
        super($type);
    }

    withExpandMessage(expandMessage: MessageAndAddressAndArea) {
        this.expandMessage = expandMessage;
        return this;
    }
}