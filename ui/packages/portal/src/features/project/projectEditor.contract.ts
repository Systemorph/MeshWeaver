import { BaseEvent } from "@open-smc/application/src/contract/application.contract";
import { contractMessage } from "@open-smc/application/src/contractMessage";

@contractMessage("OpenSmc.Notebook.OpenProjectEvent")
export class OpenProjectEvent extends BaseEvent {
    constructor(public projectId: string) {
        super();
    }
}