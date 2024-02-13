import { useEffect, useState } from "react";
import { AreaChangedEvent, SetAreaRequest } from "./contract/application.contract";
import { useSubscribeToAreaChanged } from "./useSubscribeToAreaChanged";
import { renderControl } from "./renderControl";
import { useMessageHub } from "./AddHub";
import { layoutHubId } from "./LayoutHub";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

interface ControlStarterProps {
    area: string;
    path?: string;
    options?: unknown
}

export function ControlStarter({area, path, options}: ControlStarterProps) {
    const [event, setEvent] = useState<AreaChangedEvent>();
    const hub = useMessageHub(layoutHubId);

    useSubscribeToAreaChanged(setEvent, area, layoutHubId);

    useEffect(() => {
        sendMessage(hub, new SetAreaRequest(area, path, options));
        return () => sendMessage(hub, new SetAreaRequest(area, null));
    }, [area, path, options, sendMessage]);

    if (!event?.view) {
        return null;
    }

    return renderControl(event?.view);
}