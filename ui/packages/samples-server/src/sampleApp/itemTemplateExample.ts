export const itemTemplateExample = {
    $type: "MeshWeaver.Data.EntityStore",
    reference: {
        $type: "MeshWeaver.Data.LayoutAreaReference",
        area: "LineOfBusiness"
    },
    collections: {
        "MeshWeaver.Layout.UiControl": {
            "LineOfBusiness": {
                $type: "MeshWeaver.Layout.ItemTemplateControl",
                dataContext: {
                    $type: "MeshWeaver.Data.CollectionReference",
                    collection: "LineOfBusiness"
                },
                data: {
                    $type: "MeshWeaver.Layout.DataBinding.Binding",
                    path: "$"
                },
                view: {
                    $type: "MeshWeaver.Layout.Composition.LayoutStackControl",
                    areas: [
                        {
                            $type: "MeshWeaver.Data.EntityReference",
                            collection: "MeshWeaver.Layout.UiControl",
                            id: "LineOfBusiness/DisplayName"
                        },
                        {
                            $type: "MeshWeaver.Data.EntityReference",
                            collection: "MeshWeaver.Layout.UiControl",
                            id: "LineOfBusiness/DisplayNameEditor"
                        },
                        {
                            $type: "MeshWeaver.Data.EntityReference",
                            collection: "MeshWeaver.Layout.UiControl",
                            id: "LineOfBusiness/Currencies"
                        },
                    ]
                }
            },
            "LineOfBusiness/DisplayName": {
                $type: "MeshWeaver.Layout.HtmlControl",
                data: {
                    $type: "MeshWeaver.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                }
            },
            "LineOfBusiness/DisplayNameEditor": {
                $type: "MeshWeaver.Layout.TextBoxControl",
                data: {
                    $type: "MeshWeaver.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                }
            },
            "LineOfBusiness/Currencies": {
                $type: "MeshWeaver.Layout.ItemTemplateControl",
                data: {
                    $type: "MeshWeaver.Layout.DataBinding.Binding",
                    path: "$.Currencies"
                },
                view: {
                    $type: "MeshWeaver.Layout.HtmlControl",
                    data: {
                        $type: "MeshWeaver.Layout.DataBinding.Binding",
                        path: "$"
                    }
                }
            }
        },
        LineOfBusiness: {
            "1": {
                SystemName: "1",
                DisplayName: "1",
                Currencies: ["CHF", "EUR"]
            },
            "2": {
                SystemName: "2",
                DisplayName: "2",
                Currencies: ["USD"]
            }
        }
    }
}