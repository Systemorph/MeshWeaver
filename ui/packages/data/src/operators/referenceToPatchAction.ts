import { toPointer } from "../toPointer";
import { WorkspaceReference } from "../contract/WorkspaceReference";
import { pathToPatchAction } from "./pathToPatchAction";

export const referenceToPatchAction = (reference: WorkspaceReference) =>
    pathToPatchAction(toPointer(reference.path));