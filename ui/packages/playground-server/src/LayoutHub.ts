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
import { PlaygroundWindow } from "./app/PlaygroundWindow";
import { AddToContextRequest } from "@open-smc/message-hub/src/api/AddToContextRequest";

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
                const control = this.createControlByPath(path, options);
                this.controlsByArea.set(area, control);

                this.sendMessage(new AddToContextRequest(control, control.address));
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

    private createControlByPath(path: string, options: unknown) {
        switch (path) {
            case "/":
                return new PlaygroundWindow();
            default:
                throw `Unknown path "${path}"`;
        }
    }
}