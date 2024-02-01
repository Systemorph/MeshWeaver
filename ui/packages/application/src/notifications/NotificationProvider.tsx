import { createContext, PropsWithChildren, useState } from "react";
import { useNotification } from "rc-notification";
import { notificationConfig } from "./notificationConfig";
import { makeToastApi, ToastApi } from "./makeToastApi";
import "./toast.scss";

export const notificationContext = createContext<ToastApi>(null);

export function NotificationProvider({children}: PropsWithChildren) {
    const [notificationApi, notificationContainer] = useNotification(notificationConfig);
    const [toastApi] = useState(makeToastApi(notificationApi));

    return (
        <notificationContext.Provider value={toastApi}>
            {children}
            {notificationContainer}
        </notificationContext.Provider>
    )
}