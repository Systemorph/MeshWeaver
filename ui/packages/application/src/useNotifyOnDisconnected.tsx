import { ToastApi } from "./makeToastApi";
import { useEffect } from "react";
import { useConnection } from "./Connection";

const DISCONNECTED_KEY = 'Disconnected';

export function useNotifyOnDisconnected({showToast, closeToast}: ToastApi) {
    const connection = useConnection();

    useEffect(() => {
        return connection.onDisconnected(() => {
            showToast(
                'Disconnected',
                'Trying to reconnect...',
                'Error',
                {
                    closable: false,
                    duration: null,
                    key: DISCONNECTED_KEY
                }
            );
        });
    }, [connection, showToast]);

    useEffect(() => {
        return connection.onReconnected(() => {
            closeToast(DISCONNECTED_KEY);
            showToast('Reconnected', 'Connection restored successfully.', 'Success');
        });
    }, [connection, closeToast]);
}