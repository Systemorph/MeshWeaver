import { renderControl } from "../renderControl";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import { useState, useEffect } from "react";
import { AreaChangedEvent, RefreshRequest } from "../contract/application.contract";
import { useMessageHub } from "../AddHub";
import { ControlView } from "../ControlDef";
import { contractMessage } from "../contractMessage";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

const remoteMainArea = "Data";

@contractMessage("OpenSmc.Application.Layout.RemoteViewControl")
export class RemoteViewControlDef {
    constructor(
        public readonly message: unknown,
        public readonly address: unknown) {
    }
}

export default function RemoteViewControl({data}: ControlView) {
    const [areaChangeEvent, setAreaChangeEvent] = useState<AreaChangedEvent>(data as AreaChangedEvent);
    const hub = useMessageHub();
    
    useSubscribeToAreaChanged(setAreaChangeEvent, remoteMainArea);

    useEffect(() => {
        setAreaChangeEvent(data as AreaChangedEvent);
    }, [data]);

    useEffect(() => {
        sendMessage(hub, new RefreshRequest(remoteMainArea));
    }, [hub, data]);

    if (!areaChangeEvent?.view) {
        return null;
    }

    return renderControl(areaChangeEvent.view);
}
