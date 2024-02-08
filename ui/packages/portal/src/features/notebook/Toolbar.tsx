import { Button } from "@open-smc/ui-kit/src/components/Button";
import { useStopEvaluation } from "./documentStore/hooks/useStopEvaluation";
import classNames from "classnames";
import { useRunActiveElement } from "./documentStore/hooks/useRunActiveElement";
import { useShowOutline } from "./documentStore/hooks/useShowOutline";
import { useCreateElement } from "./documentStore/hooks/useCreateElement";
import { useSelectNextElement } from "./documentStore/hooks/useSelectNextElement";
import { useRunAllElements } from "./documentStore/hooks/useRunAllElements";
import { rcTooltipOptions } from "../../shared/tooltipOptions";
import { SessionButton } from "./SessionButton";
import styles from "./header.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import { useNotebookEditorSelector, useNotebookEditorStore } from "./NotebookEditor";

export function Toolbar() {
    const elementIds = useNotebookEditorSelector('elementIds');
    const activeElementId = useNotebookEditorSelector('activeElementId');
    const {canRunActiveElement, runActiveElement} = useRunActiveElement();
    const runAllElements = useRunAllElements();
    const selectNextElement = useSelectNextElement();
    const evaluationStatus = useNotebookEditorSelector("evaluationStatus");
    const stopEvaluation = useStopEvaluation();
    const {showOutline, toggleOutline} = useShowOutline();
    const codeCollapsed = useNotebookEditorSelector("codeCollapsed");
    const createElement = useCreateElement();
    const afterElementId = activeElementId ? activeElementId : elementIds[elementIds.length - 1];
    const {setState} = useNotebookEditorStore();

    const {canEdit, canRun} = useNotebookEditorSelector("permissions");

    const hasElements = elementIds.length > 0;

    const runButton =
        <Button className={classNames(styles.toolbarButton, button.button)}
                icon="sm sm-run"
                type="button"
                disabled={!canRun || !canRunActiveElement}
                tooltip="Run the selected cells" tooltipOptions={rcTooltipOptions}
                onClick={() => {
                    const result = runActiveElement();
                    selectNextElement(activeElementId, true, 'FirstLine', true);
                    return result;
                }}
                data-qa-btn-run-cell/>;

    const runAllButton = evaluationStatus === 'Evaluating'
        ?
        <Button className={classNames(styles.toolbarButton, button.button)}
                icon="sm sm-stop"
                type="button"
                onClick={stopEvaluation}
                tooltip="Stop evaluation" tooltipOptions={rcTooltipOptions} data-qa-btn-run-cell/>
        :
        <Button className={classNames(styles.toolbarButton, button.button)}
                icon='sm sm-run-all'
                type="button"
                disabled={!canRun || !hasElements || evaluationStatus === 'Pending'}
                tooltip="Run whole notebook" tooltipOptions={rcTooltipOptions}
                onClick={() => runAllElements()} data-qa-btn-run-all/>;

    return (
        <div className={styles.row}>
            <Button icon="sm sm-plus"
                    type="button"
                    label='Code'
                    className={classNames(styles.addButton, button.button, button.secondaryButton)}
                    tooltip="Insert code below" tooltipOptions={rcTooltipOptions}
                    onClick={() =>
                        createElement("code", afterElementId)
                    }
                    disabled={!canEdit}
                    data-qa-btn-add-code/>
            <Button icon="sm sm-plus"
                    label='Text'
                    type="button"
                    className={classNames(styles.addButton, button.button, button.secondaryButton)}
                    tooltip="Insert text below" tooltipOptions={rcTooltipOptions}
                    onClick={() =>
                        createElement("markdown", afterElementId)
                    }
                    disabled={!canEdit}
                    data-qa-btn-add-text/>
            <div className={styles.runButtonsBox}>
                {runButton}
                {runAllButton}
            </div>
            <div className={styles.toggleBox}>
                <Button
                    className={classNames(styles.iconButton, styles.toolbarButton, button.button, {active: showOutline})}
                    icon='sm sm-outline'
                    type="button"
                    onClick={toggleOutline}
                    tooltip={showOutline ? 'Hide outline' : 'Show outline'}
                    tooltipOptions={rcTooltipOptions}
                    data-qa-btn-outline-show/>
                <Button
                    className={classNames(styles.iconButton, styles.toolbarButton, button.button, {active: codeCollapsed})}
                    onClick={() => {
                        setState(state => {
                            state.codeCollapsed = !state.codeCollapsed;
                        })
                    }}
                    tooltip={codeCollapsed ? 'Show code' : 'Hide code'}
                    icon={codeCollapsed ? 'sm sm-code-cell-on' : 'sm sm-code-cell-off'}
                    type="button"
                    tooltipOptions={rcTooltipOptions}
                    data-qa-btn-outline-hide/>
            </div>
            <SessionButton/>
        </div>
    );
}