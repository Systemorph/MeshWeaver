import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Data.UnsubscribeDataRequest")
export class UnsubscribeDataRequest {
    constructor(public ids: string[]) {
    }
}