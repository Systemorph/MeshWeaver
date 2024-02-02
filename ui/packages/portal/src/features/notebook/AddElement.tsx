import { Button } from "@open-smc/ui-kit/src/components/Button";
import { useCreateElement } from "./documentStore/hooks/useCreateElement";
import styles from "./outline.module.scss";
import classNames from "classnames";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"

type Props = {
    afterElementId?: string;
    alwaysVisible?: boolean
}

export function AddElement({afterElementId, alwaysVisible}: Props) {
    const createElement = useCreateElement();

    return (
        <div className={`${styles.elementMenu} ${!alwaysVisible ? 'hideable' : ''}`}>
            <div className={styles.addBox}>
                <Button className={classNames(styles.elementButton, button.button)}
                        icon="sm sm-plus"
                        label='code'
                        onClick={() => createElement("code", afterElementId)}
                        data-qa-btn-add-code/>
                <Button className={classNames(styles.elementButton, button.button)}
                        icon="sm sm-plus"
                        label='Text'
                        onClick={() => createElement("markdown", afterElementId)}
                        data-qa-btn-add-text/>
            </div>
        </div>
    );
}
