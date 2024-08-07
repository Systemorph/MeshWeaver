import { type } from "@open-smc/serialization/src/type";
import { WorkspaceReference } from "./WorkspaceReference";

@type("MeshWeaver.Data.EntireWorkspace")
export class EntireWorkspace extends WorkspaceReference {
}