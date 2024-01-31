import { FileModel } from "../fileExplorerState";
import { useCommonFileMenuItems } from "./useCommonFileMenuItems";

export function useFolderMenuItems(file: FileModel, canEdit: boolean) {
    return useCommonFileMenuItems(file, canEdit);
}