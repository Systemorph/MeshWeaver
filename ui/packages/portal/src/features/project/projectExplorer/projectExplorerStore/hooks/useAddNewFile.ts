import { compareFiles } from "../fileExplorerState";
import { ProjectNodeKind } from "../../../../../app/projectApi";
import { useGetUniqueFileName } from "./useGetUniqueFileName";
import { useFileStore, useFileExplorerStore } from "../../ProjectExplorerContextProvider";

let fileId = 0;
const getNewFileId = () => `f${fileId++}`;

export function useAddNewFile() {
    const explorerStore = useFileExplorerStore();
    const fileStore = useFileStore();
    const getUniqueFileName = useGetUniqueFileName();

    return async (kind: Extract<ProjectNodeKind, 'Folder' | 'Notebook'>) => {
        const fileId = getNewFileId();

        const files = fileStore.setState(files => {
            files[fileId] = {
                id: fileId,
                name: getUniqueFileName( kind==='Folder' ? 'New folder' : 'New notebook'),
                kind,
                editMode: true
            }
        })

        explorerStore.setState(state => {
            state.fileIds.push(fileId);
            state.fileIds.sort((a, b) => compareFiles(files[a], files[b]));
        });
    }
}