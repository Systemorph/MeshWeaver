import { Fragment, useEffect, useRef, useState } from "react";
import styles from "./mainWindow.module.scss";
import { StackView } from "./LayoutStackControl";
import { keyBy } from "lodash";
import { CloseModalDialogEvent } from "../contract/application.contract";
import Dialog from "rc-dialog";
import "@open-smc/ui-kit/src/components/dialog.scss";
import classNames from "classnames";
import { mainWindowAreas } from "./mainWindowApi";
import { basename } from "path-browserify";
import { RenderArea } from "../app/RenderArea";

export type ModalOptions = {
    isClosable?: boolean;
    size: ModalSize;
}

type ModalSize = "S" | "M" | "L";

export function MainWindow({areaIds}: StackView) {
    const mappedAreas = keyBy(areaIds, basename);

    const mainAreaId = mappedAreas[mainWindowAreas.main];
    const toolbarAreaId = mappedAreas[mainWindowAreas.toolbar];
    const sideMenuAreaId = mappedAreas[mainWindowAreas.sideMenu];
    const contextMenuAreaId = mappedAreas[mainWindowAreas.contextMenu];
    const statusBarAreaId = mappedAreas[mainWindowAreas.statusBar]
    const modalAreaId = mappedAreas[mainWindowAreas.modal];

    const [isResizing, setIsResizing] = useState(false);

    const contextPanelRef = useRef<HTMLDivElement>();
    let mPos = useRef<number>();

    function resize(e: MouseEvent) {
        const dx = mPos.current - e.x;
        const offsetDx = contextPanelRef.current.getBoundingClientRect().x - mPos.current;
        mPos.current = e.x;

        if (offsetDx > -5 && offsetDx < 5) {
            contextPanelRef.current.style.width = (parseInt(getComputedStyle(contextPanelRef.current, '').width) + dx) + "px";
        }
    }

    function onMouseDown(event: React.MouseEvent<HTMLDivElement>) {
        setIsResizing(true);
        mPos.current = event.nativeEvent.x;
        document.addEventListener("mousemove", resize, false);
    }

    useEffect(() => {
        document.addEventListener("mouseup", function () {
            setIsResizing(false);
            document.removeEventListener("mousemove", resize, false);
        }, false);
    }, [resize]);

    const contextPanelClassName = classNames(styles.contextPanel, {
        isResizing
    });

    // const modalOptions = modal?.options as ModalOptions;
    //
    // // const modalClassName = classNames('dialog-wrapper', modalOptions?.size && `size-${modalOptions.size}`);
    // // const isClosable = modalOptions?.isClosable ?? true;

    return (
        <Fragment>
            <div className={styles.layout}>
                {sideMenuAreaId &&
                    <RenderArea
                        id={sideMenuAreaId}
                        render={
                            view =>
                                <div className={styles.sideMenu}>
                                    {view}
                                </div>
                        }
                    />
                }
                {toolbarAreaId &&
                    <RenderArea
                        id={toolbarAreaId}
                        render={
                            view =>
                                <div className={styles.toolbar}>
                                    {view}
                                </div>
                        }
                    />

                }
                {mainAreaId &&
                    <RenderArea
                        id={mainAreaId}
                        render={
                            view =>
                                <div className={styles.mainContent}>
                                    {view}
                                </div>
                        }

                    />
                }
                {contextMenuAreaId &&
                    <RenderArea
                        id={contextMenuAreaId}
                        render={
                            view =>
                                <div className={contextPanelClassName} ref={contextPanelRef}>
                                    {view}
                                    <div className={styles.resizer} onMouseDown={onMouseDown}/>
                                </div>
                        }
                    />

                }
            </div>
            {/*{modal?.view &&*/}
            {/*    <Dialog*/}
            {/*        visible={true}*/}
            {/*        closable={isClosable}*/}
            {/*        closeIcon={<i className='sm sm-close'/>}*/}
            {/*        className={modalClassName}*/}
            {/*        onClose={isClosable && (() => {*/}
            {/*            setModal(null);*/}
            {/*            sendMessage(hub, new CloseModalDialogEvent());*/}
            {/*        })}*/}
            {/*        children={renderControl(modal.view)}*/}
            {/*        destroyOnClose={true}*/}
            {/*    />*/}
            {/*}*/}
        </Fragment>
    );
}
