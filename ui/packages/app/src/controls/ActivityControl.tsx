import { useMemo } from "react";
import { getInitials } from "@open-smc/utils/src/getInitials";
import { format } from "date-fns";
import styles from "./activityControl.module.scss";
import { ControlView } from "../ControlDef";
import { useClickAction } from "../useClickAction";
import classNames from "classnames";

export interface ActivityView extends ControlView {
    user?: UserInfo;
    date?: string;
    title?: string;
    color?: string;
}

export interface UserInfo {
    email: string;
    displayName: string;
    photo: string;
}

// replace summary type to HtmlControl after it is done

export default function ActivityControl({id, user, date, title, color, clickMessage}: ActivityView) {
    const initials = useMemo(() => user?.displayName ? getInitials(user.displayName) : null, [user])
    const clickAction = useClickAction(clickMessage);
    const avatar = user?.photo;

    const className = classNames(styles.container, {
        clickable: clickAction
    });

    return (
        <div id={id} className={className} onClick={clickAction}>
            <span style={{backgroundColor: color}} className={styles.colorBadge}/>
            <div className={styles.innerWrapper}>
                {(avatar || initials) && (
                    <div className={styles.userpic}>
                        {avatar && <img className={styles.photo} src={avatar}/>}
                        {!avatar && initials}
                    </div>
                )}
                <div className={styles.nameContainer}>
                    <div className={styles.username}>{user?.displayName}</div>
                    <div className={styles.name}>{title}</div>
                </div>

                <div className={styles.date}>{format(new Date(date), "LLL dd, yyyy")}</div>
            </div>
        </div>
    )
}