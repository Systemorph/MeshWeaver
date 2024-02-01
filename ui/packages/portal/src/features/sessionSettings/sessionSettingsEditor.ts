// import { ApplicationHub } from "@open-smc/application";
import { SessionSettingsChangedEvent, SessionSettingsRestoredEvent } from "./sessionSettingsEditor.contract";
import { useMemo } from "react";

interface SessionSettings {
    image: string;
    imageTag: string;
    tier: string;
    cpu: number;
    sessionIdleTimeout: number;
    applicationIdleTimeout: number;
}

export function getSessionSettingsEditor(viewModelId: string) {
    async function changeSettings(objectId: string, settings: Partial<SessionSettings>) {
        // const {image, imageTag, tier, cpu, sessionIdleTimeout, applicationIdleTimeout} = settings;
        // const response = await ApplicationHub.makeRequest(viewModelId, new SessionSettingsChangedEvent(objectId, image, imageTag, tier, cpu, sessionIdleTimeout, applicationIdleTimeout), SessionSettingsChangedEvent);
        //
        // if (response.status !== 'Committed') {
        //     throw 'Request rejected';
        // }
    }

    async function restoreInheritance(objectId: string) {
        // const response = await ApplicationHub.makeRequest(viewModelId, new SessionSettingsRestoredEvent(objectId), SessionSettingsRestoredEvent);
        //
        // if (response.status !== 'Committed') {
        //     throw 'Request rejected';
        // }
    }

    return {
        changeSettings,
        restoreInheritance
    }
}

export function useSessionSettingsEditor(viewModelId: string) {
    return useMemo(() => getSessionSettingsEditor(viewModelId), [viewModelId]);
}