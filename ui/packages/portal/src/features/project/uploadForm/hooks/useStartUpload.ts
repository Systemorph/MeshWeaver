import { ProjectApi } from "../../../../app/projectApi";
import axios from "axios";
import { useSetUploadFormFile } from "./useSetUploadFormFile";
import { useProject } from "../../projectStore/hooks/useProject";
import { useEnvironmentId } from "../../projectStore/hooks/useEnvironmentId";
import { usePath } from "../../projectExplorer/projectExplorerStore/hooks/usePath";
import { useReloadFiles } from "../../projectExplorer/projectExplorerStore/hooks/useReloadFiles";
import { useStore } from "../store";

export function useStartUpload() {
    const {getState} = useStore();
    const setUploadFormFile = useSetUploadFormFile();
    const {project} = useProject();
    const env = useEnvironmentId();
    const path = usePath();
    const reloadFiles = useReloadFiles();

    return async (fileId: string) => {
        const {files: uploadFormFiles} = getState();
        const {blob, conflict, conflictResolution, newName} = uploadFormFiles[fileId];

        if (conflict && !conflictResolution) {
            throw 'Cannot upload file until the conflict is resolved';
        }

        const cancelTokenSource = ProjectApi.cancelTokenSource();

        const onUploadProgress = (progressEvent: ProgressEvent) =>
            setUploadFormFile(fileId, {
                progressEvent
            });

        setUploadFormFile(fileId, {status: 'InProgress', cancelTokenSource});

        try {
            await ProjectApi.uploadFile(project.id, env, path, blob, onUploadProgress, cancelTokenSource.token);
            setUploadFormFile(fileId, {status: 'Complete'});
            await reloadFiles();
        } catch (error) {
            if (!axios.isCancel(error)) {
                setUploadFormFile(fileId, {status: 'Error'});
                console.error(error);
            }
        }
    }
}