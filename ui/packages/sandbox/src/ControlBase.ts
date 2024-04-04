import { ControlDef, MessageAndAddress } from "@open-smc/application/src/ControlDef";
import { Style } from "packages/application/src/contract/controls/Style";
import { SubjectHub } from "@open-smc/messaging/src/SubjectHub";
import { Subscription } from "rxjs";
import { Constructor } from "@open-smc/utils/src/Constructor";
import { MessageHandler } from "@open-smc/messaging/src/api/MessageHandler";
import { ofType } from "packages/application/src/contract/ofType";
import { v4 } from "uuid";
import { isFunction } from "lodash-es";
import { ClickedEvent } from "@open-smc/application/src/contract/application.contract";
import { MessageHub } from "@open-smc/messaging/src/api/MessageHub";
import { AddedToContext } from "@open-smc/messaging/src/api/AddedToContext";
import { addToContext } from "@open-smc/messaging/src/middleware/addToContext";

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

    private children: { hub: MessageHub, address: any }[] = [];
    private contexts: MessageHub[] = [];

    protected readonly subscription: Subscription = new Subscription();

    protected constructor(public readonly $type: string, public id = v4()) {
        super();

        this.address = `${$type}-${id}`;

        this.withMessageHandler(AddedToContext, ({message: {context}}) => {
            this.contexts.push(context);
            this.children.forEach(({hub, address}) => {
                addToContext(context, hub, address);
            })
        });
    }

    toJSON() {
        const {
            source,
            operator,
            subscription,
            input,
            output,
            children,
            contexts,
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

    protected addChildHub(hub: MessageHub, address: any) {
        this.children.push({hub, address});

        this.contexts.forEach(context => {
            addToContext(context, hub, address);
        });
    }

    protected sendMessage<T>(message: T, target?: any) {
        this.output.next({message, target});
    }

    protected handleMessage<T>(type: Constructor<T>, handler: MessageHandler<this, T>) {
        return this.input
            .pipe(ofType(type))
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

    withClickMessage(message: MessageAndAddress | Factory<this, MessageAndAddress> = {
        address: this.address,
        message: new ClickedEvent()
    }) {
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