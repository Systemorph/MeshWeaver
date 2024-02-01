import {
    CancelCommand,
    EvaluateElementsCommand,
    NotebookElementCreatedEvent,
    NotebookElementDeletedEvent,
    NotebookElementEvaluationStatusBulkEvent,
    NotebookElementEvaluationStatusEvent,
    NotebookElementMovedEvent,
    NotebookElementOutputAddedEvent,
    NotebookElementsClearOutputEvent,
    SessionEvaluationStatusChangedEvent,
    SessionStatusEvent,
    StartSessionEvent,
    StopSessionEvent
} from "./notebookEditor.contract";
import { EventStatus } from "@open-smc/application/application.contract";
import { ElementKind, SessionSpecification } from "../../../app/notebookFormat";
// import { MessageHubInstance } from "@open-smc/application/messageHub/createMessageHub";

export function getNotebookEditorApi(messageHub: MessageHubInstance) {
    const {sendMessage, receiveMessage} = messageHub;
    const requestedEvents = new Set<string>();

    // async function createElement(elementKind: ElementKind, content: string, afterElementId?: string, elementId?: string) {
    //     const event = new NotebookElementCreatedEvent(elementKind, content, afterElementId, elementId);
    //     requestedEvents.add(event.eventId);
    //
    //     await sendMessage(event);
    //
    //     let unsubscribeFromResponse: () => void;
    //     let unsubscribeFromError: () => void;
    //
    //     return new Promise<NotebookElementCreatedEvent>((resolve, reject) => {
    //         unsubscribeFromResponse = receiveMessage(
    //             NotebookElementCreatedEvent,
    //             resolve,
    //             ({eventId}) => eventId === event.eventId);
    //         unsubscribeFromError = subscribeToErrorEvent(messageHub, event, reject);
    //     }).finally(() => {
    //         unsubscribeFromResponse && unsubscribeFromResponse();
    //         unsubscribeFromError && unsubscribeFromError();
    //     });
    // }

    // function subscribeToNewElements(notebookId: string, handler: (event: NotebookElementCreatedEvent) => void) {
    //     return receiveMessage(
    //         NotebookElementCreatedEvent,
    //         (event) => {
    //             const {status, eventId} = event;
    //
    //             if (notebookId === event.notebookId && status === 'Committed') {
    //                 if (requestedEvents.has(eventId)) {
    //                     requestedEvents.delete(eventId);
    //                 } else {
    //                     return handler(event);
    //                 }
    //             }
    //         }
    //     );
    // }

    // function moveElements(elementIds: string[], afterElementId: string) {
    //     const event = new NotebookElementMovedEvent(elementIds, afterElementId);
    //     requestedEvents.add(event.eventId);
    //     return sendMessage(event);
    // }

    // function deleteElement(elementIds: string[]) {
    //     const event = ;
    //     requestedEvents.add(event.eventId);
    //     return sendMessage(event);
    // }

    // function subscribeToElementEvaluationStatus(handler: (event: NotebookElementEvaluationStatusEvent) => void) {
    //     return receiveMessage(NotebookElementEvaluationStatusEvent, handler);
    // }

    // function subscribeToElementStatusBulkEvent(handler: (event: NotebookElementEvaluationStatusBulkEvent) => void) {
    //     return receiveMessage(NotebookElementEvaluationStatusBulkEvent, handler);
    // }

    // function subscribeToSessionStatusEvent(handler: (event: SessionStatusEvent) => void) {
    //     return receiveMessage(SessionStatusEvent, handler);
    // }

    // async function evaluateElements(elementIds?: string[]) {
    //     const event = new EvaluateElementsCommand(elementIds);
    //
    //     const {status} = await sendMessageAndWaitForResponse(
    //         messageHub,
    //         event,
    //         EvaluateElementsCommand,
    //         ({eventId}) => event.eventId === eventId
    //         );
    //
    //     validateStatus(status);
    // }

    // function stopEvaluation() {
    //     return sendMessage(new CancelCommand());
    // }

    // async function startSession(specification: SessionSpecification) {
    //     const event = new StartSessionEvent(specification);
    //
    //     const {status} = await sendMessageAndWaitForResponse(
    //         messageHub,
    //         event,
    //         StartSessionEvent,
    //         ({eventId}) => event.eventId === eventId
    //     );
    //
    //     validateStatus(status);
    // }

    // function stopSession() {
    //     return sendMessage(new StopSessionEvent());
    // }
    //
    // function validateStatus(status: EventStatus) {
    //     if (status !== 'Committed') {
    //         switch (status) {
    //             case 'AccessDenied':
    //                 throw 'Access Denied';
    //             case 'InvalidSubscription':
    //                 throw 'Invalid subscription';
    //             default:
    //                 throw 'Unexpected error occurred';
    //         }
    //     }
    // }
}

