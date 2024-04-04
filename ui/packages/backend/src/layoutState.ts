export const layoutState = {
    $type: "OpenSmc.Layout.LayoutArea",
    id: "/",
    style: {},
    options: {},
    control: {
        $type: "OpenSmc.Layout.Composition.LayoutStackControl",
        skin: "MainWindow",
        areas: [
            {
                $type: "OpenSmc.Layout.LayoutArea",
                id: "/Main",
                control: {
                    $type: "OpenSmc.Layout.Views.MenuItemControl",
                    dataContext: {
                        $type: "OpenSmc.Data.JsonPathReference",
                        path: "$.menu"
                    },
                    title: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$"
                    },
                    icon: "systemorph-fill"
                }
            },
            {
                $type: "OpenSmc.Layout.LayoutArea",
                id: "/Toolbar",
                control: {
                    $type: "OpenSmc.Layout.TextBoxControl",
                    dataContext: {
                        value: "Hello world",
                        user: {
                            $type: "OpenSmc.Data.JsonPathReference",
                            path: "$.user"
                        }
                    },
                    data: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$.user.name"
                    }
                }
            },
            {
                $type: "OpenSmc.Layout.LayoutArea",
                id: "/ContextMenu",
                control: {
                    $type: "OpenSmc.Layout.TextBoxControl",
                    dataContext: {
                        $type: "OpenSmc.Data.JsonPathReference",
                        path: "$.menu"
                    },
                    data: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$"
                    }
                }
            }
        ]
    }
}