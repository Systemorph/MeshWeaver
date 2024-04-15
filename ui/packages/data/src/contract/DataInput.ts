import { PathReferenceBase } from "./PathReferenceBase";

export type DataInput = {
    [key: string]: unknown | PathReferenceBase;
}