import { useEffect, useState } from "react";
import { AreaChangedEvent, RefreshRequest, SetAreaRequest } from "./contract/application.contract";
import { useMessageHub } from "./AddHub";
import { layoutHubId } from "./LayoutHub";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import { renderControl } from "./renderControl";
import { useSubscribeToAreaChanged } from "./useSubscribeToAreaChanged";

interface ControlStarterProps {
    area: string;
    path?: string;
    options?: unknown
}

export function ControlStarter({area, path, options}: ControlStarterProps) {
    const [event, setEvent] = useState<AreaChangedEvent>();
    const layoutHub = useMessageHub(layoutHubId);

    useSubscribeToAreaChanged(layoutHub, area, setEvent);

    useEffect(() => {
        sendMessage(layoutHub, new SetAreaRequest(area, path, options));
        return () => sendMessage(layoutHub, new SetAreaRequest(area, null));
    }, [layoutHub, area, path, options]);

    useEffect(() => {
        if (event) {
            sendMessage(layoutHub, new RefreshRequest(area));
        }
    }, [event]);

    if (!event?.view) {
        return null;
    }

    return renderControl(event?.view);
}