import { renderControl } from "../renderControl";
import { AddHub, useMessageHub } from "../AddHub";
import { useControlContext } from "../ControlContext";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import { useEffect, useState } from "react";
import { AreaChangedEvent } from "../contract/application.contract";
import { ControlView } from "../ControlDef";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

export interface RedirectView extends ControlView {
    readonly message: unknown;
    readonly redirectAddress: unknown;
    readonly redirectArea: string;
}

export default function RedirectControl({message, redirectAddress, redirectArea}: RedirectView) {
    return (
        <AddHub address={redirectAddress}>
            <RedirectControlInner/>
        </AddHub>
    );
}

function RedirectControlInner() {
    const {boundView: {message, redirectArea}} = useControlContext<RedirectView>();
    const [event, setEvent] = useState<AreaChangedEvent>();
    const hub = useMessageHub();

    useSubscribeToAreaChanged(setEvent, redirectArea);

    useEffect(() => {
        sendMessage(hub, message);
    }, [sendMessage, hub, message]);

    if (!event?.view) {
        return null;
    }

    return renderControl(event.view);
}