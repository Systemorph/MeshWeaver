import { NotificationAPI } from "rc-notification";
import { Key, ReactNode } from "react";
import { OpenConfig } from "rc-notification/es/Notifications";
import classNames from "classnames";

interface ToastContentProps {
    title: ReactNode;
    content: ReactNode;
}

type ToastSeverity = 'Error' | 'Success';

function ToastContent({title, content}: ToastContentProps) {
    return (
        <div>
            <h3>{title}</h3>
            <p>{content}</p>
        </div>
    )
}

export function makeToastApi(notificationApi: NotificationAPI) {
    function showToast(title: ReactNode, message: ReactNode, severity: ToastSeverity = 'Success', config?: Partial<OpenConfig>) {
        const className = classNames('severity-' + severity);

        notificationApi.open({
            content: <ToastContent title={title} content={message}/>,
            className,
            duration: 3,
            closable: true,
            ...config
        });
    }

    function closeToast(key: Key) {
        notificationApi.close(key);
    }

    function closeAllToasts() {
        notificationApi.destroy();
    }

    return {
        showToast,
        closeToast,
        closeAllToasts
    };
}

export type ToastApi = ReturnType<typeof makeToastApi>;