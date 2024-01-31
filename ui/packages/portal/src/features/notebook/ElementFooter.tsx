import { PropsWithChildren } from "react";
import styles from "./element.module.scss";
interface Props {
    language: string;
}

export function ElementFooter({language, children}: PropsWithChildren<Props>) {
    return (
        <div className={styles.footer} data-qa-footer>
            {children}
        </div>
    )
}