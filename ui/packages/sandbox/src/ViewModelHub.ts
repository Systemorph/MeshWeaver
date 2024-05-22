import { MessageHubBase } from "@open-smc/application/src/messageHub/MessageHubBase";
import { ControlDef } from "@open-smc/application/src/ControlDef";

export class ViewModelHub extends MessageHubBase {
    setArea(area: string, control: ControlDef, options?: unknown) {
        return this.sendMessage();
    }
}

