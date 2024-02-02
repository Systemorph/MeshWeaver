import styles from './formHeader.module.scss';
import { ReactNode } from "react";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import classNames from "classnames";
import buttons from "@open-smc/ui-kit/src/components/buttons.module.scss"

type Props = {
    text: string,
    onClose: () => void,
    button?: ReactNode,
};

export function FormHeader({text, button, onClose}: Props) {
    return (
        <div className={styles.header}>
            <h1 className={styles.title} data-qa-title>{text}</h1>
            <div className={styles.container}>
                {button}
                <Button
                    icon='sm sm-close'
                    className={classNames(buttons.button, styles.closeButton)}
                    onClick={onClose}
                    data-qa-btn-close
                />
            </div>
        </div>
    );
}