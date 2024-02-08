import { createStore } from "@open-smc/store/src/store";
import { AttachSmappEvent, RunSmappEvent, SmappStatusEvent } from "./smapp.contract";
import { PropsWithChildren, useEffect, useState } from "react";
// import { ApplicationHub } from "@open-smc/application";
import { getSmappApi, SmappApi } from "./smappApi";
import { ProjectApi } from "../../app/projectApi";
import { useThrowAsync } from "@open-smc/utils/src/useThrowAsync";


export interface SmappContext extends NotebookContext<SmappState> {
    readonly smappApi: SmappApi;
}

export interface SmappState extends NotebookState {
    readonly smappStatusEvent: SmappStatusEvent;
}

interface SmappContextProviderProps {
    projectId: string;
    envId: string;
    notebookPath: string;
    sessionId: string
}

export function SmappContext({projectId, envId, notebookPath, sessionId, children}: PropsWithChildren<SmappContextProviderProps>) {
    const [isLoading, setLoading] = useState(true);
    const [smappContext, setSmappContext] = useState<SmappContext>();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                setLoading(true);
                const {viewModelId, smappApi, smappStatusEvent} = await getInitialState(projectId, envId, notebookPath, sessionId);
                setSmappContext({
                    smappApi,
                    store: createStore<SmappState>({
                        viewModelId,
                        isRunning: true,
                        smappStatusEvent,
                    })
                });
                setLoading(false);
            } catch (error) {
                throwAsync(error);
            }
        })();
    }, [projectId, envId, notebookPath]);

    useEffect(() => {
        if (smappContext) {
            const {smappApi: {subscribeToSmappStatusChange}, store: {setState}} = smappContext;
            return subscribeToSmappStatusChange(smappStatusEvent => {
                setState(state => {
                    state.smappStatusEvent = smappStatusEvent;
                });
            });
        }
    }, [smappContext]);

    if (isLoading) {
        return <div>Loading...</div>;
    }

    return (
        <notebookContext.Provider value={smappContext} children={children}/>
    );
}

async function getInitialState(projectId: string, envId: string, notebookPath: string, sessionId: string) {
    const node = await ProjectApi.getNode(projectId, envId, notebookPath);
    const viewModelId = await getViewModel(projectId, envId, node.id, sessionId);

    const smappApi = getSmappApi(viewModelId);

    const smappStatusEvent = await smappApi.getSmappStatus(viewModelId);

    return {
        viewModelId,
        smappApi,
        smappStatusEvent
    };
}

async function getViewModel(projectId: string, environment: string, notebookId: string, sessionId: string) {
    const {
        viewModelId,
        isNew
    } = await ApplicationHub.getOrCreateViewModel('Smapp', `${projectId}/${environment}/${notebookId}`);

    if (isNew) {
        if (sessionId) {
            await ApplicationHub.makeRequest(viewModelId, new AttachSmappEvent(sessionId), AttachSmappEvent);
        }
        else {
            await ApplicationHub.makeRequest(viewModelId, new RunSmappEvent(projectId, environment, notebookId), RunSmappEvent);
        }
    }

    return viewModelId;
}