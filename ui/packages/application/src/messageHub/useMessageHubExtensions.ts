import { ClassType } from "../contractMessage";
import { useMessageHub } from "./AddHub";
import { BaseEvent, ErrorEvent } from "../application.contract";
import { withTimeout } from "@open-smc/utils/promiseWithTimeout";
import { MessageDelivery } from "../SignalrHub";

// TODO: legacy (8/3/2023, akravets)

export function useMessageHubExtensions(id?: string) {
    const {sendMessage, receiveMessage} = useMessageHub(id);

    function sendMessageAndWaitForResponse<TMessage>(message: any,
                                                     properties: Record<string, unknown>,
                                                     responseType: ClassType<TMessage>,
                                                     responseFilter?: (envelope: MessageDelivery<TMessage>) => boolean,
                                                     timeout?: number) {
        const result = waitForMessage(responseType, responseFilter, timeout);

        sendMessage(message, properties);

        return result;
    }

    function subscribeToErrorEvent<T extends BaseEvent>(sourceEvent: T, handler: (message: string) => void) {
        return receiveMessage<ErrorEvent<T>>(
            ErrorEvent,
            ({message}) => handler(message),
            ({message: {sourceEvent: {eventId}}}) => eventId === sourceEvent.eventId
        );
    }

    function waitForMessage<TMessage>(
        messageType: ClassType<TMessage>,
        filter?: (response: MessageDelivery<TMessage>) => boolean,
        timeout?: number) {
        const resultPromise = new Promise<TMessage>((resolve) => {
            const removeHandler = receiveMessage(
                messageType,
                response => {
                    resolve(response);
                    removeHandler();
                },
                filter);
        });

        return timeout ? withTimeout(resultPromise, timeout) : resultPromise;
    }

    async function makeRequest<TMessage>(
        message: any,
        responseType: ClassType<TMessage>,
        responseFilter?: (envelope: MessageDelivery<TMessage>) => boolean,
        timeout?: number) {
        const requestId = getNewRequestId();
        return sendMessageAndWaitForResponse(
            message,
            {requestId},
            responseType,
            (response) =>
                response?.properties.requestId === requestId && responseFilter?.(response),
            timeout
        );
    }

    return {
        sendMessageAndWaitForResponse,
        subscribeToErrorEvent,
        waitForMessage,
        makeRequest
    }
}

let requestNumber = Date.now();

function getNewRequestId() {
    return (requestNumber++).toString();
}

