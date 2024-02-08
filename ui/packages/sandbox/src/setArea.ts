import { Observer } from "rxjs";
import { MessageDelivery } from "@open-smc/application/src/messageHub/MessageDelivery";
import { ControlDef } from "@open-smc/application/src/ControlDef";
import { sendMessage } from "@open-smc/application/src/messageHub/sendMessage";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";

export function setArea(
    this: Observer<MessageDelivery>,
    area: string, controlDef: ControlDef,
    options?: unknown) {
    sendMessage.bind(this)(new AreaChangedEvent(area, controlDef, options));
}