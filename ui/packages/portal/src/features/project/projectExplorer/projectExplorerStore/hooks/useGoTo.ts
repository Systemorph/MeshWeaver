import { compareFiles, CURRENT_FOLDER } from "../fileExplorerState";
import { useProject } from "../../../projectStore/hooks/useProject";
import { SessionStorageWrapper } from "../../../../../shared/utils/sessionStorageWrapper";
import { useEnv } from "../../../projectStore/hooks/useEnv";
import { ProjectApi } from "../../../../../app/projectApi";
import { keyBy, map } from "lodash";
import { useFileExplorerStore, useFileStore } from "../../ProjectExplorerContextProvider";

export function useGoTo() {
    const {project} = useProject();
    const {envId} = useEnv();
    const fileExplorerStore = useFileExplorerStore();
    const fileStore = useFileStore();

    return async (path?: string) => {
        fileExplorerStore.setState(state => {
            state.loading = true;
        });

        const files = await ProjectApi.getProjectFiles(project.id, envId, path);

        files.sort(compareFiles);
        const fileIds = map(files, "id");

        SessionStorageWrapper.setItem(CURRENT_FOLDER, {projectId: project.id, envId: envId, path});

        fileStore.setState(keyBy(files, "id"));

        fileExplorerStore.setState(state => {
            state.path = path;
            state.fileIds = fileIds;
            state.loading = false;
        });
    }
}