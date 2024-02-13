import { Observer } from "rxjs";
import { AreaChangedEvent } from "@open-smc/application/src/contract/application.contract";
import { MessageDelivery } from "@open-smc/message-hub/src/api/MessageDelivery";
import { ControlDef } from "@open-smc/application/src/ControlDef";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

export function setArea(
    observer: Observer<MessageDelivery>,
    area: string,
    controlDef: ControlDef,
    options?: unknown) {
    sendMessage(observer, new AreaChangedEvent(area, controlDef, options));
}