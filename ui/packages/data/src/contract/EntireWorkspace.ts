import { type } from "@open-smc/serialization/src/type";
import { PathReferenceBase } from "./PathReferenceBase";

@type("OpenSmc.Data.EntireWorkspace")
export class EntireWorkspace extends PathReferenceBase {
    constructor() {
        super();
    }

    protected get path() {
        return "";
    }
}