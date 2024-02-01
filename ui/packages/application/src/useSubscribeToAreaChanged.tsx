import { AreaChangedEvent } from "./application.contract";
import { useMessageHub } from "./messageHub/AddHub";
import { useEffect } from "react";
import { MessageHub } from "./messageHub/MessageHub";

export function useSubscribeToAreaChanged<TOptions = unknown>(
    handler: (event: AreaChangedEvent<TOptions>) => void,
    area?: string,
    messageHub?: MessageHub
) {
    const defaultMessageHub = useMessageHub();
    const {receiveMessage} = messageHub || defaultMessageHub;

    useEffect(() => {
        return receiveMessage(AreaChangedEvent, handler, ({message}) => !area || message.area === area);
    }, [receiveMessage, handler, area]);
}