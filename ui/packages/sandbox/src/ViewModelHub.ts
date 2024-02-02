import { MessageHubBase } from "@open-smc/application/src/messageHub/MessageHubBase";
import { ControlDef } from "@open-smc/application/src/ControlDef";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";

export class ViewModelHub extends MessageHubBase {
    setArea(area: string, control: ControlDef, options?: unknown) {
        return this.sendMessage(new AreaChangedEvent(area, control, options));
    }
}