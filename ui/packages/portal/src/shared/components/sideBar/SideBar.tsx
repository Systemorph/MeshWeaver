import styles from "./sidebar.module.scss";
import { PropsWithChildren } from "react";

export const SideBar = ({children}: PropsWithChildren<{}>) => (
    <div className={styles.content} data-qa-side-bar>
        {children}
    </div>
)

export const ButtonGroups = ({children}: PropsWithChildren<{}>) => (
    <div className={styles.side}>
        {children}
    </div>
)

export const ButtonGroup = ({children}: PropsWithChildren<{}>) => (
    <ul className={styles.list}>
        {children}
    </ul>
)

export const Divider = ()=> <div className={styles.divider}/>