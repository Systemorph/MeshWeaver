import { BaseEvent } from "@open-smc/application/application.contract";
import { contractMessage } from "@open-smc/application/contractMessage";

@contractMessage("OpenSmc.Notebook.OpenProjectEvent")
export class OpenProjectEvent extends BaseEvent {
    constructor(public projectId: string) {
        super();
    }
}