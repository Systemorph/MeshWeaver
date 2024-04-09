import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";
import { EntityStore } from "./EntityStore";

@type("OpenSmc.Data.LayoutAreaReference")
export class LayoutAreaReference extends WorkspaceReference<EntityStore> {
    constructor(public area: string) {
        super(null);
    }

    options: {}

    apply = () => {
        throw "Should never be resolved by ui"
    }
}