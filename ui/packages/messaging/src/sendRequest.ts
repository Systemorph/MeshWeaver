import { v4 } from "uuid";
import { filter, map, take } from "rxjs";
import { MessageHub } from "./api/MessageHub";
import { messageOfType } from "./operators/messageOfType";
import { Request } from "./api/Request";
import { unpack } from "./operators/unpack";

export const sendRequest = <T>(hub: MessageHub, message: Request<T>) => {
    const id = v4();

    const result$ =
        hub
            .pipe(filter(messageOfType(message.responseType)))
            .pipe(filter(({properties}) => properties?.requestId === id))
            .pipe(take(1))
            .pipe(map(unpack));

    hub.next({
        id,
        message
    });

    return result$;
}

