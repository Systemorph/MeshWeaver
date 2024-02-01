import { MessageAndAddress } from "./ControlDef";
import { useMessageHub } from "./messageHub/AddHub";

export function useClickAction(clickMessage: MessageAndAddress) {
    const {sendMessage} = useMessageHub();

    if (!clickMessage) {
        return null;
    }

    return () => {
        const {message, address} = clickMessage;
        sendMessage(message, {target: address});
    };
}