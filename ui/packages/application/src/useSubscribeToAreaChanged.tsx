import { AreaChangedEvent } from "./application.contract";
import { useMessageHub } from "./AddHub";
import { useEffect } from "react";
import { receiveMessage } from "@open-smc/message-hub/src/receiveMessage";
import { ofType } from "./ofType";

export function useSubscribeToAreaChanged<TOptions = unknown>(
    handler: (event: AreaChangedEvent<TOptions>) => void,
    area?: string,
    hubId?: string
) {
    const hub = useMessageHub(hubId);

    useEffect(() => {
        return receiveMessage(
            hub.pipe(ofType(AreaChangedEvent)),
            handler,
            ({message}) => !area || message.area === area
        );
    }, [receiveMessage, handler, area]);
}