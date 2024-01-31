import { SmappStatusEvent } from "./smapp.contract";
import { ApplicationHub } from "@open-smc/application";

export type SmappApi = ReturnType<typeof getSmappApi>;

export function getSmappApi(viewModelId: string) {
    function subscribeToSmappStatusChange(handler: (event: SmappStatusEvent) => void) {
        return ApplicationHub.onMessage(
            viewModelId,
            SmappStatusEvent,
            handler,
            ({status}) => status === 'Committed'
        );
    }

    function getSmappStatus(viewModelId: string) {
        const event = new SmappStatusEvent();

        return ApplicationHub.sendMessageAndWaitForResponse(
            viewModelId,
            event,
            SmappStatusEvent,
            ({eventId, status}) =>
                eventId === event.eventId && status === 'Committed'
        );
    }

    return {
        subscribeToSmappStatusChange,
        getSmappStatus
    }
}