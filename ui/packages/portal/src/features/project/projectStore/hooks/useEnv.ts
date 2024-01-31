import { CurrentEnv, loadEnv, ProjectState } from "../projectState";
import { useSelector, useProjectStore } from "../projectStore";
import { useProject } from "./useProject";

const envSelector = ({currentEnv}: ProjectState) => currentEnv;

export function useEnv() {
    const {getState, setState, notify} = useProjectStore();
    const {project} = useProject();
    const {envId, isLoading, env, error} = useSelector(envSelector);

    function setEnv(newCurrentEnv: CurrentEnv) {
        setState({currentEnv: newCurrentEnv});
        notify(envSelector);
    }

    async function setEnvId(envId: string) {
        setEnv({envId, isLoading: true});
        // TODO: send envId to backend (11/19/2022, akravets)

        const {env, error} = await loadEnv(project.id, envId);

        const {currentEnv} = getState();

        if (currentEnv.envId === envId) {
            await setEnv({envId, env, error});
        }
    }

    return {envId, isLoading, env, error, setEnvId, setEnv};
}