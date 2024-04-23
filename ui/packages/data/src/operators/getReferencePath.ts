import { WorkspaceReference } from "../contract/WorkspaceReference";
import { EntityReference } from "../contract/EntityReference";
import { CollectionReference } from "../contract/CollectionReference";
import { PathReference } from "../contract/PathReference";
import { EntireWorkspace } from "../contract/EntireWorkspace";
import { JsonPointer } from "json-ptr";

export function getReferencePath(reference: WorkspaceReference) {
    if (reference instanceof EntityReference) {
        return JsonPointer.create([reference.collection, reference.id]).toString();
    }

    if (reference instanceof CollectionReference) {
        return JsonPointer.create([reference.collection]).toString();
    }

    if (reference instanceof PathReference) {
        return reference.path;
    }

    if (reference instanceof EntireWorkspace) {
        return "";
    }

    throw `Cannot get path for reference ${reference}`
}