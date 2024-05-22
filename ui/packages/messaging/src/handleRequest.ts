import { MessageDelivery } from "./api/MessageDelivery";
import { Request } from "./api/Request";
import { filter, from, map, mergeMap, Observable, ObservableInput } from "rxjs";
import { messageOfType } from "./operators/messageOfType";
import { pack } from "./operators/pack";

export const handleRequest =
    <TRequest extends Request<TResponse>, TResponse>(
        requestType: new (...args: any) => TRequest,
        handler: <T extends MessageDelivery<TRequest>>(envelope: T) => ObservableInput<TResponse>
    ) =>
        (source: Observable<MessageDelivery>) =>
            source
                .pipe(filter(messageOfType(requestType)))
                .pipe(
                    mergeMap(
                        envelope =>
                            from(handler(envelope))
                                .pipe(
                                    map(
                                        pack({
                                            properties: {
                                                requestId: envelope.id
                                            }
                                        })
                                    )
                                )
                    )
                )
                // .pipe(log("handleRequest"));

