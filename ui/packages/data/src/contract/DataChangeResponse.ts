import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Data.DataChangeResponse")
export class DataChangeResponse {
    constructor(public status: DataChangeStatus) {
    }
}

export type DataChangeStatus = "Committed" | "Failed";