// import { Dialog } from "primereact/dialog";
// import { useStopEvaluation } from "./documentStore/hooks/useStopEvaluation";
import React from 'react';
// import { Button } from "@open-smc/ui-kit/components/Button";
// import loader from "@open-smc/ui-kit/components/loader.module.scss";
// import { useNotebookEditorSelector } from "./NotebookEditor";
// import styles from "./session-dialog.module.scss";
// import classNames from "classnames";
// import button from "@open-smc/ui-kit/components/buttons.module.scss";
// import { renderControl } from "@open-smc/application/renderControl";
//
// // todo must be completed properly #25469
//
// export function NotebookSessionDialog() {
//     const dialog = useNotebookEditorSelector('sessionDialog');
//     const showDialog = dialog != null;
//
//     const stopEvaluation = useStopEvaluation();
//
//     const content = showDialog ? renderControl(dialog) : null;
//
//     return (
//         <Dialog
//             header="Session Dialog"
//             className={styles.sessionDialog}
//             closable={true}
//             visible={showDialog}
//             onShow={null}
//             onHide={null}
//         >
//             <React.Suspense fallback={<div className={loader.loading}>Loading...</div>}>
//                 {content}
//                 <Button onClick={stopEvaluation}
//                         icon="sm sm-close"
//                         label="Cancel"
//                         className={classNames(button.cancelButton, styles.cancel, button.button)}>
//                 </Button>
//             </React.Suspense>
//         </Dialog>
//     );
// }