import { useProjectStore } from "../projectStore";
import { useAccessControlEditorApi } from "./useAccessControlEditorApi";
import { useProject } from "./useProject";
import { getProjectPermissions } from "../projectState";
import { permissionsSelector } from "./useProjectPermissions";

export function useSubscribeToProjectPermissionsChange() {
    const {setState, notify} = useProjectStore();
    const {project} = useProject();
    const accessControlEditorApi = useAccessControlEditorApi();

    return () => {
        return accessControlEditorApi.subscribeToProjectPermissionChanges(project.id,async () => {
            const permissions = await getProjectPermissions(project.id);
            setState({permissions});
            notify(permissionsSelector);
        });
    }
}