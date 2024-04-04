import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";

@type("OpenSmc.Data.EntityStore")
export class EntityStore {
    reference: WorkspaceReference;
    instances: Record<string, Record<string, UiControl>>
}