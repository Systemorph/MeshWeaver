import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";
import {
    AreaChangedEvent,
    ClickedEvent,
    Dispose,
    SetAreaRequest
} from "@open-smc/application/src/contract/application.contract";
import { ControlBase } from "@open-smc/sandbox/src/ControlBase";
import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { Subscription } from "rxjs";
import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import { Constructor } from "@open-smc/utils/src/Constructor";
import { MessageHandler } from "@open-smc/message-hub/src/api/MessageHandler";
import { ofContractType } from "@open-smc/application/src/contract/ofContractType";
import { uiAddress } from "./contract";
import { AddToContext } from "@open-smc/message-hub/src/middleware/addToContext";

export class LayoutHub extends SubjectHub {
    private readonly controlsByArea = new Map<string, ControlBase>();
    private subscription = new Subscription();

    constructor() {
        super();

        this.subscription.add(
            this.handleMessage(
                SetAreaRequest,
                    envelope => this.handleSetAreaRequest(envelope)
            )
        );
    }

    private handleMessage<T>(type: Constructor<T>, handler: MessageHandler<this, T>) {
        return this.input
            .pipe(ofContractType(type))
            .subscribe(handler.bind(this));
    }

    private sendMessage<T>(message: T, target?: any) {
        this.output.next({message, target});
    }

    private handleSetAreaRequest({message}: MessageDelivery<SetAreaRequest>) {
        const {area, path, options} = message;

        const address = this.controlsByArea.get(area);

        if (path) {
            if (!address) {
                const control = this.makeControl(path, options);
                this.controlsByArea.set(area, control);

                this.sendMessage(new AddToContext(control, control.address));
                this.sendMessage(new AreaChangedEvent(area, control), uiAddress);
            }
        } else {
            this.controlsByArea.delete(area);
            // TODO: send Dispose if there is no more references (get this info from context?) (2/13/2024, akravets)
            // if (address) {
            //     this.sendMessage(new Dispose(), address);
            // }
        }
    }

    private makeControl(path: string, options: unknown) {
        switch (path) {
            default:
                return this.createLayout();
        }
    }

    private createLayout() {
        const address = "StartButton";

        return makeMenuItem()
            .withTitle("Say hello")
            .withColor("#0171ff")
            .withAddress(address)
            .withClickMessage({address, message: new ClickedEvent("1", "Hello")})
            .withMessageHandler(ClickedEvent, ({message}) => {
                this.sendMessage(message.payload, uiAddress);
            });
    }
}