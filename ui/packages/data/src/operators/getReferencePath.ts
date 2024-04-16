import { WorkspaceReference } from "../contract/WorkspaceReference";
import { EntityReference } from "../contract/EntityReference";
import { CollectionReference } from "../contract/CollectionReference";
import { PathReference } from "../contract/PathReference";
import { EntireWorkspace } from "../contract/EntireWorkspace";

export function getReferencePath(reference: WorkspaceReference) {
    if (reference instanceof EntityReference) {
        return `/${reference.collection}/${reference.id}`;
    }

    if (reference instanceof CollectionReference) {
        return `/${reference.collection}`
    }

    if (reference instanceof PathReference) {
        return reference.path;
    }

    if (reference instanceof EntireWorkspace) {
        return "";
    }

    throw `Cannot get path for reference ${reference}`
}