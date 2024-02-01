import styles from "./wrapInput.module.scss";
import classNames from "classnames";
import { forwardRef } from "react";
import { ProgressSpinner } from "./ProgressSpinner";

type Props = {
    processing?: boolean;
    invalid?: boolean;
    check?: boolean;
};

export function wrapInput<T>(render: (props: T, inputClassNames: string) => JSX.Element) {
    return forwardRef((props: T & Props, ref) => {
        const {processing, invalid, check, ...inputProps} = props;

        const inputClassNames = classNames(
            styles.input,
            invalid ? styles.invalid : '',
            check ? styles.check : '',
            processing ? styles.check : ''
        );

        return (
            <div className={styles.inputGroup}>
                {render(inputProps as T, inputClassNames)}
                {processing &&
                    <ProgressSpinner className={styles.spinner}
                                     style={{width: '14px', height: '14px'}}/>}
                {check && !invalid && !processing &&
                    <i className="sm sm-check"/>}
            </div>
        );
    })
}