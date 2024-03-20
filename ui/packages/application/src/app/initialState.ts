import { RootState } from "./store";

export const initialState: RootState = {
    rootArea: "/",
    areas: {
        "/": {
            id: "/",
            control: {
                componentTypeName: "LayoutStackControl",
                props: {
                    skin: "MainWindow",
                    areaIds: [
                        "/Main",
                        "/Toolbar"
                    ]
                }
            }
        },
        "/Main": {
            id: "/Main",
            control: {
                componentTypeName: "MenuItemControl",
                props: {
                    title: "Hello world",
                    icon: "systemorph-fill"
                }
            }
        },
        "/Toolbar": {
            id: "",
            control: {
                componentTypeName: "TextBoxControl",
                props: {
                    data: "Hello world"
                }
            }
        }
    }
}