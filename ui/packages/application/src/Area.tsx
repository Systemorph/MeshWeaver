import { JSX, useEffect, useState } from "react";
import { AreaChangedEvent, RefreshRequest } from "./contract/application.contract";
import { renderControl } from "./renderControl";
import { MessageHub } from "@open-smc/message-hub/src/api/MessageHub";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import { useSubscribeToAreaChanged } from "./useSubscribeToAreaChanged";

interface AreaProps {
    hub: MessageHub;
    event: AreaChangedEvent;
    render?: (renderedView: JSX.Element) => JSX.Element;
}

export function Area({hub, event: {area, view: initialView}, render}: AreaProps) {
    const [view, setView] = useState(initialView);

    useEffect(() => setView(initialView), [initialView]);

    useSubscribeToAreaChanged(hub, area, ({view}) => setView(view));

    useEffect(() => {
        sendMessage(hub, new RefreshRequest(area));
    }, [hub, area]);

    if (!view) {
        return null;
    }

    return render ? render(renderControl(view)) : renderControl(view);
}