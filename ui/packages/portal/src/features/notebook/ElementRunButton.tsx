import { useEvaluateElements } from "./documentStore/hooks/useEvaluateElements";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { NotebookElementModel } from "./documentStore/documentState";
import styles from "./element.module.scss";
import { ProgressSpinner } from "@open-smc/ui-kit/src/components/ProgressSpinner";
import { useElementsStore } from "./NotebookEditor";

interface ElementButtonProps {
    model: NotebookElementModel;
    disabled: boolean;
}

export function ElementRunButton({model, disabled}: ElementButtonProps) {
    const {element, isEditMode} = model;
    const {id, elementKind, evaluationStatus, evaluationCount} = element;
    const evaluateElements = useEvaluateElements();
    const elementsStore = useElementsStore();

    if (elementKind === 'code') {
        switch (evaluationStatus) {
            case 'Idle':
                return (
                    <div className={styles.evaluationWrapper} data-qa-btn-run>
                        {!!evaluationCount && <span className={styles.evaluationCount} data-qa-evaluation-count={evaluationCount}>[{evaluationCount}]</span>}
                        <Button className={styles.runButton}
                                icon="sm sm-run-circle"
                                disabled={disabled}
                                onClick={() => evaluateElements([id])}/>
                    </div>
                );
            case 'Pending':
                return <span className={styles.pendingStatus} data-qa-status={evaluationStatus}><i className="sm sm-clock"/></span>;
            case 'Evaluating':
                return <span className={styles.evaluationSpinner} data-qa-status={evaluationStatus}>
                        <ProgressSpinner className={styles.spinner}/>
                    </span>;
            default:
                return null;
        }
    } else {
        if (isEditMode) {
            return <Button className={styles.runButton}
                           icon="sm sm-run-circle"
                           onClick={() => {
                               elementsStore.setState(models => {
                                   models[id].isEditMode = false;
                               });
                           }}/>
        }
        return null;
    }
}