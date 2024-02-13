import { useEffect, useState, JSX } from "react";
import { AreaChangedEvent } from "./contract/application.contract";
import { useMessageHub } from "./AddHub";
import { renderControl } from "./renderControl";
import { receiveMessage } from "@open-smc/message-hub/src/receiveMessage";
import { ofContractType } from "./contract/ofContractType";

interface AreaProps {
    event: AreaChangedEvent;
    render?: (renderedView: JSX.Element) => JSX.Element;
}

export function Area({event: initialEvent, render}: AreaProps) {
    const [event, setEvent] = useState(initialEvent);
    const hub = useMessageHub();

    const {area, view} = event;

    useEffect(() => {
        setEvent(initialEvent);
    }, [initialEvent]);

    useEffect(() => {
        receiveMessage(
            hub.pipe(ofContractType(AreaChangedEvent)),
            setEvent,
            ({message}) => message.area === area
        );
    }, [hub, area]);

    if (!view) {
        return null;
    }

    return render ? render(renderControl(view)) : renderControl(view);
}