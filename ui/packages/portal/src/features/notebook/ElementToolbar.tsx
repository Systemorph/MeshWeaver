import { Button } from "@open-smc/ui-kit/components/Button";
import { NotebookElementModel } from "./documentStore/documentState";
import { useDeleteElementsAction } from "./documentStore/hooks/useDeleteElementsAction";
import { useMoveElements } from './documentStore/hooks/useMoveElements';
import styles from "./element.module.scss";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import classNames from "classnames";
import { useNotebookEditorSelector } from "./NotebookEditor";

interface Props {
    model: NotebookElementModel;
}

export function ElementToolbar({model: {element}}: Props) {
    const elementIds = useNotebookEditorSelector('elementIds');
    const deleteElementsAction = useDeleteElementsAction();
    const moveElements = useMoveElements();

    return (
        <div className={styles.cellToolbar} data-qa-toolbar>
            <div className={styles.toolbarInner}>
                <Button className={classNames(styles.toolbarButton, button.button)}
                        icon="sm sm-arrow-up"
                        disabled={elementIds.indexOf(element.id) === 0}
                        onClick={() =>
                            moveElements(
                                [element.id],
                                elementIds[elementIds.indexOf(element.id) - 2]
                            )
                        }
                        data-qa-btn-up
                />
                <Button className={classNames(styles.toolbarButton, button.button)}
                        icon="sm sm-arrow-down"
                        disabled={elementIds.indexOf(element.id) === elementIds.length - 1}
                        onClick={() =>
                            moveElements(
                                [element.id],
                                elementIds[elementIds.indexOf(element.id) + 1]
                            )
                        }
                        data-qa-btn-down
                />
                <Button className={classNames(styles.toolbarButton, styles.deleteButton, button.button)}
                        icon="sm sm-trash"
                        onClick={() => deleteElementsAction([element.id])}
                        data-qa-btn-delete
                />
            </div>
        </div>
    );
}