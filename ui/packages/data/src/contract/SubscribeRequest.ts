import { type } from "@open-smc/serialization/src/type";
import { Request } from "@open-smc/messaging/src/api/Request";
import { DataChangedEvent } from "./DataChangedEvent";
import { WorkspaceReference } from "./WorkspaceReference";

@type("MeshWeaver.Data.SubscribeRequest")
export class SubscribeRequest extends Request<DataChangedEvent> {
    constructor(public reference: WorkspaceReference) {
        super(DataChangedEvent)
    }
}