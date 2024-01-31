import { useSelector, useProjectStore } from "../projectStore";
import { ProjectState } from "../projectState";
import { ProjectApi } from "../../../../app/projectApi";

const selector = (state: ProjectState) => state?.project;

export function useProject() {
    const {setState, notify} = useProjectStore();
    const project = useSelector(selector);
    const {id: projectId} = project;

    async function reloadProject() {
        const newProject = await ProjectApi.getProject(project.id);
        setState({project: newProject});
        notify(selector);
    }

    return {projectId, project, reloadProject};
}