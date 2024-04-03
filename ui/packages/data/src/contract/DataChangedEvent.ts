import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Data.DataChangedEvent")
export class DataChangedEvent {
    constructor(public id: string, public change: object) {
    }
}