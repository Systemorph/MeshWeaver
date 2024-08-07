import { type } from "@open-smc/serialization/src/type";

@type("MeshWeaver.Application.UiAddress")
export class UiAddress {
    constructor(public id: string) {
    }
}