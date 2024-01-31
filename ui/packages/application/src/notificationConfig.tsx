import { CSSMotionProps } from "rc-motion";

export const motion: CSSMotionProps = {
    motionName: 'rc-notification-fade',
    motionAppear: true,
    motionEnter: true,
    motionLeave: true,
    onLeaveStart: (ele) => {
        const {offsetHeight} = ele;
        return {height: offsetHeight};
    },
    onLeaveActive: () => ({height: 0, opacity: 0, margin: 0}),
}
export const notificationConfig = {motion};