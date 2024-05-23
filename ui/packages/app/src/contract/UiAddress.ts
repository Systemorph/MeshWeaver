import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Application.UiAddress")
export class UiAddress {
    constructor(public id: string) {
    }
}