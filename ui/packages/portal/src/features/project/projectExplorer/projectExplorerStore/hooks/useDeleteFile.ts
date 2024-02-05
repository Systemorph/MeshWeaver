import { useProjectStore as useProjectStore } from "../../../projectStore/projectStore";
import { without } from "lodash";
import { useNavigate } from "react-router-dom";
import { useFileStore, useFileExplorerStore } from "../../ProjectExplorerContextProvider";
import { useMessageHubExtensions } from "@open-smc/application/src/messageHub/useMessageHubExtensions";
import { DeleteNodeEvent } from "../../projectExplorerEditorGrain/projectExplorerEditor.contract";

export function useDeleteFile() {
    const fileExplorerStore = useFileExplorerStore();
    const fileStore = useFileStore();
    const projectStore = useProjectStore();
    const navigate = useNavigate();
    const {makeRequest} = useMessageHubExtensions();

    return async (fileId: string) => {
        const {path, kind} = fileStore.getState()[fileId];
        const {activeFile, project, currentEnv} = projectStore.getState();

        fileStore.setState(files => {
            delete files[fileId];
        });

        fileExplorerStore.setState(state => {
            state.fileIds = without(state.fileIds, fileId);
        });

        await makeRequest(new DeleteNodeEvent(path), DeleteNodeEvent);

        if (kind === 'Folder' && activeFile?.path.startsWith(path)) {
            const link = `/project/${project.id}/env/${currentEnv.envId}`;
            navigate(link);
        }
    }
}