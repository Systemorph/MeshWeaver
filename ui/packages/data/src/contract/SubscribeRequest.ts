import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Data.SubscribeRequest")
export class SubscribeRequest {
    constructor(public id: string, public workspaceReference: unknown) {
    }
}