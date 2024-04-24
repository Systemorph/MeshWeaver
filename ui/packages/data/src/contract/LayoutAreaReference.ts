import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("OpenSmc.Data.LayoutAreaReference")
export class LayoutAreaReference extends WorkspaceReference {
    options: {}

    constructor(public area: string) {
        super();
    }
}