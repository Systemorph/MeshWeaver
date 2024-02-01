import { useMessageHub } from "./messageHub/AddHub";
import { v4 as uuid } from "uuid";
import { CategoryItemsRequest, CategoryItemsResponse, Named, ErrorEvent, SetSelectionRequest } from "./application.contract";

export function useCategoryApi() {
    const {sendMessage, receiveMessage} = useMessageHub();

    return {
        async sendCategoryRequest(category: string, search?: string, page?: number, pageSize?: number) {
            const requestId = uuid();

            let unsubscribeFromDropdownResponse: () => void;
            let unsubscribeFromError: () => void;

            const promise = new Promise<Named[]>((resolve) => {
                unsubscribeFromDropdownResponse = receiveMessage(
                    CategoryItemsResponse,
                    (x) => resolve(x.result),
                    // TODO: eventId should be introduced, otherwise this doesn't guarantee the uniqueness of event (8/16/2022, akravets)
                    (x) => x.properties?.requestId === requestId);

                unsubscribeFromError = receiveMessage<ErrorEvent<CategoryItemsResponse>>(
                    ErrorEvent,
                    () => resolve(null),
                    // TODO: eventId should be introduced, otherwise this doesn't guarantee the uniqueness of event (8/16/2022, akravets)
                    ({properties}) => properties.requestId === requestId);
            }).finally(() => {
                unsubscribeFromDropdownResponse && unsubscribeFromDropdownResponse();
                unsubscribeFromError && unsubscribeFromError();
            });

            sendMessage(new CategoryItemsRequest(category, search, page, pageSize), {id: requestId});

            return promise;
        },

        sendCategoryChange(selection: Record<string, string[]>) {
            const requestId = uuid();
            sendMessage(new SetSelectionRequest(selection), {id: requestId});
        },
    }
}
