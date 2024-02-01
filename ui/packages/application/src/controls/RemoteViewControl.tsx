import { renderControl } from "../renderControl";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import { useState, useEffect } from "react";
import { AreaChangedEvent, RefreshRequest } from "../application.contract";
import { useMessageHub } from "../messageHub/AddHub";
import { ControlView } from "../ControlDef";
import { contractMessage } from "../contractMessage";

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
    const {sendMessage} = useMessageHub();
    
    useSubscribeToAreaChanged(setAreaChangeEvent, remoteMainArea);

    useEffect(() => {
        setAreaChangeEvent(data as AreaChangedEvent);
        sendMessage(new RefreshRequest(remoteMainArea));
    }, [data, sendMessage]);

    if (!areaChangeEvent?.view) {
        return null;
    }

    return renderControl(areaChangeEvent.view);
}
