import { useContext } from "react";
import { notificationContext } from "./NotificationProvider";

export function useToast() {
    return useContext(notificationContext);
}