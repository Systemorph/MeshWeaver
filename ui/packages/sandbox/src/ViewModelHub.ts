import { MessageHub } from "@open-smc/application/messageHub/MessageHub";
import { ControlDef } from "@open-smc/application/ControlDef";
import { AreaChangedEvent } from "@open-smc/application/application.contract";

export class ViewModelHub extends MessageHub {
    setArea(area: string, control: ControlDef, options?: unknown) {
        return this.sendMessage(new AreaChangedEvent(area, control, options));
    }
}