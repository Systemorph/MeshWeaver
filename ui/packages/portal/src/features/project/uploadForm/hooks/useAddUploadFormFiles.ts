import { UploadFileModel, useStore } from "../store";
import { keyBy } from "lodash";
import { fileIdsSelector } from "./useUploadFormFileIds";
import { filesSelector } from "./useUploadFormFiles";
import { useStartUpload } from "./useStartUpload";
import { useNodeExists } from "../../projectExplorer/projectExplorerStore/hooks/useNodeExists";

let id = 0;
const getNewId = () => `f-${id++}`;

export function useAddUploadFormFiles() {
    const {getState, setState, notify} = useStore();
    const nodeExists = useNodeExists();
    const startUpload = useStartUpload();

    return (blobs: File[]) => {
        const {files, fileIds} = getState();

        const newFileModels = blobs.map<UploadFileModel>(blob => {
            const {exists: conflict, canReplace} = nodeExists(blob.name);

            const status = !conflict || canReplace ? 'New' : 'Error';

            return ({
                id: getNewId(),
                blob,
                status,
                conflict
            });
        });

        const newFiles = {...files, ...keyBy(newFileModels, 'id')};
        const newFileIds = [...fileIds, ...newFileModels.map(f => f.id)];

        setState({files: newFiles, fileIds: newFileIds});
        notify(filesSelector);
        notify(fileIdsSelector);

        newFileModels.forEach(({id, status, conflict}) => {
            if (status === 'New' && !conflict) {
                startUpload(id);
            }
        })
    }
}