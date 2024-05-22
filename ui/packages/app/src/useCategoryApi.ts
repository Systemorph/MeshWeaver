import { v4 as uuid } from "uuid";
import { CategoryItemsRequest, CategoryItemsResponse, Named, ErrorEvent, SetSelectionRequest }
    from "@open-smc/layout/src/contract/application.contract";
import { receiveMessage } from "@open-smc/messaging/src/receiveMessage";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { IMessageHub } from "@open-smc/messaging/src/MessageHub";
import { filter } from "rxjs";

export function useCategoryApi() {
    const hub: IMessageHub = null;

    return {
        async sendCategoryRequest(category: string, search?: string, page?: number, pageSize?: number) {
            const requestId = uuid();

            let unsubscribeFromDropdownResponse: () => void;
            let unsubscribeFromError: () => void;

            const promise = new Promise<Named[]>((resolve) => {
                unsubscribeFromDropdownResponse = receiveMessage(
                    hub.pipe(
                        filter(messageOfType(CategoryItemsResponse))
                    ),
                    (x) => resolve(x.result),
                    // TODO: eventId should be introduced, otherwise this doesn't guarantee the uniqueness of event (8/16/2022, akravets)
                    (x) => x.properties?.requestId === requestId);

                unsubscribeFromError = receiveMessage<ErrorEvent<CategoryItemsResponse>>(
                    hub.pipe(
                        filter(messageOfType(ErrorEvent))
                    ),
                    () => resolve(null),
                    // TODO: eventId should be introduced, otherwise this doesn't guarantee the uniqueness of event (8/16/2022, akravets)
                    ({ properties }) => properties.requestId === requestId);
            }).finally(() => {
                unsubscribeFromDropdownResponse && unsubscribeFromDropdownResponse();
                unsubscribeFromError && unsubscribeFromError();
            });

            sendMessage(hub, new CategoryItemsRequest(category, search, page, pageSize), { id: requestId });

            return promise;
        },

        sendCategoryChange(selection: Record<string, string[]>) {
            const requestId = uuid();
            sendMessage(hub, new SetSelectionRequest(selection), { id: requestId });
        },
    }
}
