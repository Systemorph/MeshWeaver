import { contractMessage } from "@open-smc/application/src/contractMessage";
import { BaseEvent } from "@open-smc/application/src/application.contract";

@contractMessage("OpenSmc.Notebook.SessionSettings.SessionSettingsChangedEvent")
export class SessionSettingsChangedEvent extends BaseEvent {
    constructor(public objectId: string,
                public image: string,
                public imageTag: string,
                public tier: string,
                public cpu: number,
                public sessionIdleTimeout: number,
                public applicationIdleTimeout: number) {
        super();
    }
}

@contractMessage("OpenSmc.Notebook.SessionSettings.SessionSettingsRestoredEvent")
export class SessionSettingsRestoredEvent extends BaseEvent {
    constructor(public objectId: string) {
        super();
    }
}