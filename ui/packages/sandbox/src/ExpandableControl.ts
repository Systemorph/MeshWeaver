import { ExpandRequest } from "@open-smc/application/application.contract";
import { Constructor } from "@open-smc/utils/Builder";
import { ExpandableView, MessageAndAddressAndArea } from "@open-smc/application/ControlDef";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export type ExpandAction = (request: ExpandRequest, control: ControlBase) => void;

export abstract class ExpandableControl extends ControlBase implements ExpandableView {
    expandMessage: MessageAndAddressAndArea;
    readonly expandAction: ExpandAction;

    protected constructor($type: string) {
        super($type);

        this.receiveMessage(ExpandRequest, (request: ExpandRequest) => {
            this.expandAction?.(request, this);
        });

        if (this.expandMessage) {
            this.expandMessage = null;
        }
    }
}

export class ExpandableControlBuilder<TControl extends ExpandableControl> extends ControlBuilderBase<TControl> {
    constructor(ctor: Constructor<TControl>) {
        super(ctor);
    }

    withExpandMessage(expandMessage: MessageAndAddressAndArea) {
        this.data.expandMessage = expandMessage;
        return this;
    }

    withExpandAction(action: ExpandAction, area = "expand", payload?: unknown) {
        this.withExpandMessage({
            message: new ExpandRequest(this.data.id, area, payload),
            address: this,
            area
        });

        this.data.expandAction = action;
        return this;
    }

    build() {
        const instance = super.build();

        if (instance.expandMessage) {
            instance.expandMessage = {
                ...instance.expandMessage,
                address: instance
            }
        }

        return instance
    }
}