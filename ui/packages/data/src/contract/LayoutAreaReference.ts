import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReferenceBase } from "./WorkspaceReferenceBase";

@type("OpenSmc.Data.LayoutAreaReference")
export class LayoutAreaReference extends WorkspaceReferenceBase {
    options: {}

    constructor(public area: string) {
        super();
    }

    get(data: unknown) {
        throw 'Should never be used';
    }
}