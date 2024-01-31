import { getUniqueFileName } from "../../../../../shared/utils/getUniqueFileName";
import { useFileExplorerStore, useFileStore } from "../../ProjectExplorerContextProvider";

export function useGetUniqueFileName(): (baseName: string) => string {
    const fileExplorerStore = useFileExplorerStore();
    const fileStore = useFileStore();

    return (baseName: string) => {
        const files = fileStore.getState();
        const {fileIds} = fileExplorerStore.getState();

        const fileNames = fileIds
            .filter((fileId)=> !files[fileId].editMode)
            .map((fileId) => files[fileId].name);

        return getUniqueFileName(baseName, fileNames);
    }
}