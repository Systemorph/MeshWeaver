import { WorkspaceReference } from "../contract/WorkspaceReference";
import { EntityReference } from "../contract/EntityReference";
import { CollectionReference } from "../contract/CollectionReference";
import { EntireWorkspace } from "../contract/EntireWorkspace";
import { PathReference } from "../contract/PathReference";

export function isPathReference(reference: WorkspaceReference) {
    return reference instanceof EntityReference || reference instanceof CollectionReference
        || reference instanceof EntireWorkspace || reference instanceof PathReference;
}