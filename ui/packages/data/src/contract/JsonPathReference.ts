import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.JsonPathReference")
export class JsonPathReference<T> extends WorkspaceReference<T> {
    constructor(public path: string) {
        super();
    }
}