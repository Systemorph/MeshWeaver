import { ControlDef, MessageAndAddress } from "@open-smc/application/src/ControlDef";
import { Style } from "@open-smc/application/src/Style";
import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";
import { Subscription } from "rxjs";
import { Constructor } from "@open-smc/utils/src/Constructor";
import { MessageHandler } from "@open-smc/message-hub/src/api/MessageHandler";
import { ofContractType } from "@open-smc/application/src/contract/ofContractType";
import { v4 } from "uuid";
import { isFunction } from "lodash";
import { ClickedEvent } from "@open-smc/application/src/contract/application.contract";

export abstract class ControlBase extends SubjectHub implements ControlDef {
    readonly moduleName: string;
    readonly apiVersion: string;

    dataContext: unknown;
    address: string;

    clickMessage: MessageAndAddress;
    style: Style;
    className: string;
    skin: string;
    tooltip: string;
    data: unknown;
    isReadonly?: boolean;
    label?: string;

    protected readonly subscription: Subscription = new Subscription();

    protected constructor(public readonly $type: string, public id = v4()) {
        super();

        this.address = `${$type}-${id}`;
    }

    toJSON() {
        const {
            subscription,
            input,
            output,
            ...result
        } = this;
        return result;
    }

    withMessageHandler<T>(type: Constructor<T>, handler: MessageHandler<this, T>) {
        this.subscription.add(
            this.handleMessage(type, handler)
        );
        return this;
    }

    protected sendMessage<T>(message: T, target?: any) {
        this.output.next({message, target});
    }

    protected handleMessage<T>(type: Constructor<T>, handler: MessageHandler<this, T>) {
        return this.input
            .pipe(ofContractType(type))
            .subscribe(handler.bind(this));
    }

    withId(id: string) {
        this.id = id;
        return this;
    }

    withAddress(address: any) {
        this.address = address;
        return this;
    }

    withData(data: unknown) {
        this.data = data;
        return this;
    }

    withDataContext(dataContext: unknown) {
        this.dataContext = dataContext;
        return this;
    }

    // withStyle(buildFunc: (builder: Style) => void) {
    //     this.style = buildFunc(makeStyle());
    //     return this;
    // }

    withClassName(value: string) {
        this.className = value;
        return this;
    }

    // withFlex(buildFunc?: (builder: StyleBuilder) => void) {
    //     this.data.styleBuilder = buildFunc?.(makeStyle().withDisplay("flex"))
    //     return this;
    // }

    withSkin(value: string) {
        this.skin = value;
        return this;
    }

    withClickMessage(message: MessageAndAddress | Factory<this, MessageAndAddress> = {address: this.address, message: new ClickedEvent()}) {
        this.clickMessage = isFunction(message) ? message.apply(this) : message;
        return this;
    }

    withTooltip(tooltip: string) {
        this.tooltip = tooltip;
        return this;
    }

    withLabel(label: string) {
        this.label = label;
        return this;
    }

    isReadOnly(isReadOnly: boolean) {
        this.isReadonly = isReadOnly;
        return this;
    }
}

type Factory<TControl, T> = (this: TControl) => T;