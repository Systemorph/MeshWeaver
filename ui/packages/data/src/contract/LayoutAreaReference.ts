import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.LayoutAreaReference")
export class LayoutAreaReference extends WorkspaceReference {
    constructor(public area: string) {
        super();
    }

    options: {}

    toJsonPath(): string {
        throw 'This reference should never be resolved on UI';
    }
}