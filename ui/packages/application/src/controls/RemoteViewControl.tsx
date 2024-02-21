import { renderControl } from "../renderControl";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import { useState, useEffect } from "react";
import { AreaChangedEvent, RefreshRequest } from "../contract/application.contract";
import { useMessageHub } from "../AddHub";
import { ControlView } from "../ControlDef";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

const areaName = "Data";

interface RemoteViewView extends ControlView {
    data: AreaChangedEvent;
}

export default function RemoteViewControl({data}: RemoteViewView) {
    const [event, setEvent] = useState(data);
    const hub = useMessageHub();

    useSubscribeToAreaChanged(hub, areaName, setEvent);

    useEffect(() => setEvent(data), [data]);

    useEffect(() => {
        sendMessage(hub, new RefreshRequest(areaName));
    }, [hub]);

    if (!event?.view) {
        return null;
    }

    return renderControl(event.view);
}
