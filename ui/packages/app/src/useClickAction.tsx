import { MessageAndAddress } from "./ControlDef";
import { useMessageHub } from "./AddHub";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";

export function useClickAction(clickMessage: MessageAndAddress) {
    const hub = useMessageHub();

    if (!clickMessage) {
        return null;
    }

    return () => {
        const {message, address} = clickMessage;
        sendMessage(hub, message, {target: address});
    };
}