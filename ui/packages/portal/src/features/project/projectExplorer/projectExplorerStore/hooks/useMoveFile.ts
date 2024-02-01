import { useEnv } from "../../../projectStore/hooks/useEnv";
import { useProject } from "../../../projectStore/hooks/useProject";
import { useNavigate } from "react-router-dom";
import { ProjectNodeMoveEvent } from "../../projectExplorerEditorGrain/projectExplorerEditor.contract";
import { useMessageHubExtensions } from "@open-smc/application/messageHub/useMessageHubExtensions";
import { useFileExplorerStore, useFileStore } from "../../ProjectExplorerContextProvider";
import { useProjectSelector, useProjectStore } from "../../../projectStore/projectStore";

export function useMoveFile() {
    const fileStore = useFileStore();
    const fileExplorerStore = useFileExplorerStore();
    const projectStore = useProjectStore();
    const {makeRequest} = useMessageHubExtensions();
    const navigate = useNavigate();
    const {project} = useProject();
    const {env} = useEnv();
    const activeFile = useProjectSelector("activeFile")

    return async (fileId: string, newPath: string) => {
        const {path, kind} = fileStore.getState()[fileId];

        await makeRequest(new ProjectNodeMoveEvent(path, newPath), ProjectNodeMoveEvent);

        const {[fileId]: file} = fileStore.setState(files => {
            files[fileId].path = newPath;
        });

        if (kind === "Notebook" || kind === "Blob") {
            if (fileId === activeFile?.id) {
                projectStore.setState(state => {
                    state.activeFile = file;
                });

                navigate(`/project/${project.id}/env/${env.id}/${newPath}`);
            }
        } else if (kind === "Folder") {
            if (activeFile?.path.startsWith(path)) {
                const {activeFile: {path}} = projectStore.setState(state => {
                    state.activeFile.path = state.activeFile.path.replace(path, newPath)
                });
                navigate(`/project/${project.id}/env/${env.id}/${path}`);
            }
        }
    }
}