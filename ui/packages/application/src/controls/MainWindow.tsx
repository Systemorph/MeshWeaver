import React, { Fragment, useEffect, useRef, useState} from "react";
import { renderControl } from "../renderControl";
import { useSubscribeToAreaChanged } from "../useSubscribeToAreaChanged";
import styles from "./mainWindow.module.scss";
import { StackView } from "./LayoutStackControl";
import { keyBy } from "lodash";
import { AreaChangedEvent, CloseModalDialogEvent } from "../application.contract";
import Dialog from "rc-dialog";
import "@open-smc/ui-kit/components/dialog.scss";
import classNames from "classnames";
import { useMessageHub } from "../messageHub/AddHub";

export const mainWindowAreas = {
    main: "Main",
    toolbar: "Toolbar",
    sideMenu: "SideMenu",
    contextMenu: "ContextMenu",
    modal: "Modal",
    statusBar: "StatusBar"
} as const;

export type MainWindowArea = typeof mainWindowAreas[keyof typeof mainWindowAreas];

export type ModalOptions = {
    isClosable?: boolean;
    size: ModalSize;
}

type ModalSize = "S" | "M" | "L";

export function MainWindow({id, areas}: StackView) {
    const areasByKey = keyBy(areas, "area") as Record<MainWindowArea, AreaChangedEvent>;

    const [main, setMain] = useState(areasByKey[mainWindowAreas.main]);
    const [toolbar, setToolbar] = useState(areasByKey[mainWindowAreas.toolbar]);
    const [sideMenu, setSideMenu] = useState(areasByKey[mainWindowAreas.sideMenu]);
    const [contextMenu, setContextMenu] = useState(areasByKey[mainWindowAreas.contextMenu]);
    const [statusBar, setStatusBar] = useState(areasByKey[mainWindowAreas.statusBar]);
    const [modal, setModal] = useState(areasByKey[mainWindowAreas.modal]);

    useSubscribeToAreaChanged(setMain, mainWindowAreas.main);
    useSubscribeToAreaChanged(setToolbar, mainWindowAreas.toolbar);
    useSubscribeToAreaChanged(setSideMenu, mainWindowAreas.sideMenu);
    useSubscribeToAreaChanged(setContextMenu, mainWindowAreas.contextMenu);
    useSubscribeToAreaChanged(setStatusBar, mainWindowAreas.statusBar);
    useSubscribeToAreaChanged(setModal, mainWindowAreas.modal);

    const [isResizing, setIsResizing] = useState(false);

    useEffect(() => {
        const areasByKey = keyBy(areas, "area") as Record<MainWindowArea, AreaChangedEvent>;

        setMain(areasByKey[mainWindowAreas.main]);
        setToolbar(areasByKey[mainWindowAreas.toolbar]);
        setSideMenu(areasByKey[mainWindowAreas.sideMenu]);
        setContextMenu(areasByKey[mainWindowAreas.contextMenu]);
        setStatusBar(areasByKey[mainWindowAreas.statusBar]);
        setModal(areasByKey[mainWindowAreas.modal]);
    }, [areas]);

    const {sendMessage} = useMessageHub();

    const modalOptions = modal?.options as ModalOptions;

    const modalClassName = classNames('dialog-wrapper', modalOptions?.size && `size-${modalOptions.size}`);
    const isClosable = modalOptions?.isClosable ?? true;

    const contextPanelRef = useRef<HTMLDivElement>();
    let mPos = useRef<number>();

    function resize(e: MouseEvent){
        const dx = mPos.current - e.x;
        const offsetDx = contextPanelRef.current.getBoundingClientRect().x - mPos.current;
        mPos.current = e.x;

        if(offsetDx > -5 && offsetDx < 5) {
            contextPanelRef.current.style.width = (parseInt(getComputedStyle(contextPanelRef.current, '').width) + dx) + "px";
        }
    }

    function onMouseDown(event: React.MouseEvent<HTMLDivElement>){
        setIsResizing(true);
        mPos.current = event.nativeEvent.x;
        document.addEventListener("mousemove", resize, false);
    }

    useEffect(() => {
        document.addEventListener("mouseup", function(){
            setIsResizing(false);
            document.removeEventListener("mousemove", resize, false);
        }, false);
    }, [resize]);

    const contextPanelClassName = classNames(styles.contextPanel, {
        isResizing
    });

    return (
        <Fragment>
            <div id={id} className={styles.layout}>
                {sideMenu?.view &&
                    <div className={styles.sideMenu}>
                        {renderControl(sideMenu.view)}
                    </div>
                }
                {toolbar?.view &&
                    <div className={styles.toolbar}>
                        {renderControl(toolbar.view)}
                    </div>
                }
                {main?.view &&
                    <div className={styles.mainContent}>
                        {renderControl(main.view)}
                    </div>
                }
                {contextMenu?.view &&
                    <div className={contextPanelClassName} ref={contextPanelRef}>
                        {renderControl(contextMenu.view)}
                        <div className={styles.resizer} onMouseDown={onMouseDown}/>
                    </div>
                }
            </div>
            {modal?.view &&
                <Dialog
                    visible={true}
                    closable={isClosable}
                    closeIcon={<i className='sm sm-close'/>}
                    className={modalClassName}
                    onClose={isClosable && (() => {
                        setModal(null);
                        sendMessage(new CloseModalDialogEvent());
                    })}
                    children={renderControl(modal.view)}
                    destroyOnClose={true}
                />
            }
        </Fragment>
    );
}
