import { useEffect, useState } from "react";
import { AreaChangedEvent } from "./application.contract";
import { useMessageHub } from "./messageHub/AddHub";
import { renderControl } from "./renderControl";

interface AreaProps {
    event: AreaChangedEvent;
    render?: (renderedView: JSX.Element) => JSX.Element;
}

export function Area({event: initialEvent, render}: AreaProps) {
    const [event, setEvent] = useState(initialEvent);
    const {receiveMessage} = useMessageHub();

    const {area, view} = event;

    useEffect(() => {
        setEvent(initialEvent);
    }, [initialEvent]);

    useEffect(() => {
        receiveMessage(
            AreaChangedEvent,
            setEvent,
            ({message}) => message.area === area
        );
    }, [receiveMessage, area]);

    if (!view) {
        return null;
    }

    return render ? render(renderControl(view)) : renderControl(view);
}