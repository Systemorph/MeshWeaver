import { FileModel } from "../fileExplorerState";
import { useCommonFileMenuItems } from "./useCommonFileMenuItems";

export function useFileMenuItems(file: FileModel, canEdit: boolean) {
    return useCommonFileMenuItems(file, canEdit);
}