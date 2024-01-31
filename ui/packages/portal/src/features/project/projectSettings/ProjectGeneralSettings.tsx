import { EditProjectForm } from "../editProject/EditProjectForm";
import { useProject } from "../projectStore/hooks/useProject";
import { useProjectPermissions } from "../projectStore/hooks/useProjectPermissions";

export function ProjectGeneralSettings() {
    const {reloadProject} = useProject();
    const {canEdit} = useProjectPermissions();

    return (
        <EditProjectForm onUpdated={() => reloadProject()} canEdit={canEdit}/>
    );
}