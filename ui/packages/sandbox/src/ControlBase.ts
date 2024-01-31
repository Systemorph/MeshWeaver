import { ViewModelHub } from "./ViewModelHub";
import { ControlDef, MessageAndAddress } from "@open-smc/application/ControlDef";
import { Style } from "@open-smc/application/Style";
import { v4 } from "uuid";
import { ClickedEvent } from "@open-smc/application/application.contract";
import { Builder, Constructor } from "@open-smc/utils/Builder";
import { StyleBuilder } from "./StyleBuilder";

export type ClickAction = <TView>(payload: unknown, control: ControlBase) => void;

export abstract class ControlBase extends ViewModelHub implements ControlDef {
    readonly id: string;
    readonly moduleName: string;
    readonly apiVersion: string;
    readonly address: unknown;
    readonly dataContext: unknown;

    readonly clickMessage: MessageAndAddress;
    readonly style: Style;
    readonly className: string;
    readonly skin: string;
    readonly tooltip: string;
    readonly data: unknown;
    readonly isReadonly?: boolean;
    readonly label?: string;

    readonly clickAction: ClickAction;

    protected constructor(public $type: string) {
        super();

        this.id = v4();

        this.address = this;

        this.receiveMessage(ClickedEvent, ({payload}) => {
            this.clickAction?.(payload, this);
        });
    }
}

export class ControlBuilderBase<TControl extends ControlBase = any> extends Builder<TControl> {
    constructor(ctor: Constructor<TControl>) {
        super(ctor);
    }

    withId(id: string) {
        this.data.id = id;
        return this;
    }

    withData(data: unknown) {
        this.data.data = data;
        return this;
    }

    withDataContext(dataContext: unknown) {
        this.data.dataContext = dataContext;
        return this;
    }

    withStyle(buildFunc: (builder: StyleBuilder) => void) {
        const builder = new StyleBuilder();
        buildFunc(builder);
        this.data.style = builder.build();
        return this;
    }

    withClassName(value: string) {
        this.data.className = value;
        return this;
    }

    withFlex(buildFunc?: (builder: StyleBuilder) => void) {
        const builder = new StyleBuilder();
        builder.withDisplay("flex");
        buildFunc?.(builder);
        this.data.style = builder.build();
        return this;
    }

    withSkin(value: string) {
        this.data.skin = value;
        return this;
    }

    withClickMessage(message: MessageAndAddress) {
        this.data.clickMessage = message;
        return this;
    }

    withClickAction(action: ClickAction, payload?: unknown) {
        this.data.clickMessage = {
            message: new ClickedEvent(this.data.id, payload),
            address: this
        };

        this.data.clickAction = action;
        return this;
    }

    withTooltip(tooltip: string) {
        this.data.tooltip = tooltip;
        return this;
    }

    withLabel(label: string) {
        this.data.label = label;
        return this;
    }

    isReadOnly(isReadOnly: boolean) {
        this.data.isReadonly = isReadOnly;
        return this;
    }
}