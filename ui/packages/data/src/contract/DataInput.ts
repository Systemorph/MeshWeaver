import { WorkspaceReference } from "./WorkspaceReference";


export type DataInput = {
    [key: string]: unknown | WorkspaceReference;
}