import {Avatar as PrAvatar} from "primereact/avatar";
import styles from "./avatar.module.scss";

export function Avatar() {
    return (
        <PrAvatar className={styles.avatar} size="xlarge" shape="circle"/>
    );
}