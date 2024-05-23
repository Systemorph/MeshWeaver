export const mainWindowAreas = {
    main: "Main",
    toolbar: "Toolbar",
    sideMenu: "SideMenu",
    contextMenu: "ContextMenu",
    modal: "Modal",
    statusBar: "StatusBar"
} as const;

export type MainWindowArea = typeof mainWindowAreas[keyof typeof mainWindowAreas];