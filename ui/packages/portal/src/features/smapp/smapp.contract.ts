import { BaseEvent, contractMessage } from "@open-smc/application";
import { PresenterSpec } from "@open-smc/rendering";

@contractMessage("OpenSmc.Notebook.RunSmappEvent")
export class RunSmappEvent extends BaseEvent {
    constructor(public readonly projectId: string,
                public readonly environment: string,
                public readonly notebookId: string) {
        super();
    }
}

@contractMessage("OpenSmc.Notebook.AttachSmappEvent")
export class AttachSmappEvent extends BaseEvent {
    constructor(public readonly sessionId: string) {
        super();
    }
}

@contractMessage("OpenSmc.Notebook.SmappStatusEvent")
export class SmappStatusEvent extends BaseEvent {
    readonly smappStatus: SmappStatus;
    readonly moduleReferences: Record<string, string>;
    readonly data: PresenterSpec;
}

export type SmappStatus = 'Uninitialized' | 'Initializing' | 'Ready' | 'Failed' | 'Stopped';