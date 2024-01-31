import { useProject } from "../../../projectStore/hooks/useProject";
import { useEnv } from "../../../projectStore/hooks/useEnv";
import { useFileExplorerSelector, useFileExplorerStore, useFileStore } from "../../ProjectExplorerContextProvider";
import { ProjectApi } from "../../../../../app/projectApi";
import { compareFiles, getFileModel } from "../fileExplorerState";
import { keyBy, map } from "lodash";

export function useReloadFiles() {
    const fileExplorerStore = useFileExplorerStore();
    const fileStore = useFileStore();
    const {projectId} = useProject();
    const {envId} = useEnv()
    const path = useFileExplorerSelector("path");

    return async () => {
        const files = await ProjectApi.getProjectFiles(projectId, envId, path);
        const models = files.map(getFileModel);
        models.sort(compareFiles);

        fileStore.setState(keyBy(models, "id"));

        fileExplorerStore.setState(state => {
            state.fileIds = map(models, "id");
        });
    }
}