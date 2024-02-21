import React, { Fragment, useEffect, useRef, useState } from "react";
import { renderControl } from "../renderControl";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import styles from "./mainWindow.module.scss";
import { StackView } from "./LayoutStackControl";
import { keyBy } from "lodash";
import { AreaChangedEvent, CloseModalDialogEvent } from "../contract/application.contract";
import Dialog from "rc-dialog";
import "@open-smc/ui-kit/src/components/dialog.scss";
import classNames from "classnames";
import { useMessageHub } from "../AddHub";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import { MainWindowArea, mainWindowAreas } from "./mainWindowApi";
import { Area } from "../Area";

export type ModalOptions = {
    isClosable?: boolean;
    size: ModalSize;
}

type ModalSize = "S" | "M" | "L";

export function MainWindow({id, areas}: StackView) {
    const areasByKey = keyBy(areas, "area") as Record<MainWindowArea, AreaChangedEvent>;

    const main = areasByKey[mainWindowAreas.main];
    const toolbar = areasByKey[mainWindowAreas.toolbar];
    const sideMenu = areasByKey[mainWindowAreas.sideMenu];
    const contextMenu = areasByKey[mainWindowAreas.contextMenu];
    const statusBar = areasByKey[mainWindowAreas.statusBar]
    const modal = areasByKey[mainWindowAreas.modal];

    const hub = useMessageHub();

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
            <div id={id} className={styles.layout}>
                {sideMenu &&
                    <Area
                        hub={hub}
                        event={sideMenu}
                        render={
                            view =>
                                <div className={styles.sideMenu}>
                                    {view}
                                </div>
                        }
                    />
                }
                {toolbar &&
                    <Area
                        hub={hub}
                        event={toolbar}
                        render={
                            view =>
                                <div className={styles.toolbar}>
                                    {view}
                                </div>
                        }
                    />

                }
                {main &&
                    <Area
                        hub={hub}
                        event={main}
                        render={
                            view =>
                                <div className={styles.mainContent}>
                                    {view}
                                </div>
                        }
                    />
                }
                {contextMenu &&
                    <Area
                        hub={hub}
                        event={contextMenu}
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
