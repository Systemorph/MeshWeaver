import { type } from "@open-smc/serialization/src/type";
import { Request } from "@open-smc/messaging/src/api/Request";
import { DataChangedEvent } from "./DataChangedEvent";
import { WorkspaceReferenceBase } from "./WorkspaceReferenceBase";

@type("OpenSmc.Data.SubscribeRequest")
export class SubscribeRequest extends Request<DataChangedEvent> {
    constructor(public reference: WorkspaceReferenceBase) {
        super(DataChangedEvent)
    }
}