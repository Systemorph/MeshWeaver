import { MessageDelivery } from "./api/MessageDelivery";
import { Request } from "./api/Request";
import { filter, from, map, mergeMap, Observable, ObservableInput } from "rxjs";
import { messageOfType } from "./operators/messageOfType";
import { pack } from "./operators/pack";

export const handleRequest =
    <TRequest extends Request<TResponse>, TResponse>(
        requestType: new (...args: any) => TRequest,
        handler: <T extends TRequest>(message: T) => ObservableInput<TResponse>
    ) =>
        (source: Observable<MessageDelivery>) =>
            source
                .pipe(filter(messageOfType(requestType)))
                .pipe(
                    mergeMap(
                        ({id, message}) =>
                            from(handler(message))
                                .pipe(
                                    map(
                                        pack({
                                            properties: {
                                                requestId: id
                                            }
                                        })
                                    )
                                )
                    )
                )
                // .pipe(log("handleRequest"));

