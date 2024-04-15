import { toPointer } from "../toPointer";
import { PathReferenceBase } from "../contract/PathReferenceBase";
import { pathToPatchAction } from "./pathToPatchAction";

export const referenceToPatchAction = (reference: PathReferenceBase) =>
    pathToPatchAction(toPointer(reference.path));