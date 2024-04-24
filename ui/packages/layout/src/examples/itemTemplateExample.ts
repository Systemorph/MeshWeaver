export const itemTemplateExample = {
    $type: "OpenSmc.Data.EntityStore",
    reference: {
        $type: "OpenSmc.Data.LayoutAreaReference",
        area: "LineOfBusiness"
    },
    collections: {
        "OpenSmc.Layout.UiControl": {
            "LineOfBusiness": {
                $type: "OpenSmc.Layout.ItemTemplateControl",
                dataContext: {
                    $type: "OpenSmc.Data.CollectionReference",
                    collection: "LineOfBusiness"
                },
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
                    path: "$"
                },
                view: {
                    $type: "OpenSmc.Layout.Composition.LayoutStackControl",
                    areas: [
                        {
                            $type: "OpenSmc.Data.EntityReference",
                            collection: "OpenSmc.Layout.UiControl",
                            id: "LineOfBusiness/DisplayName"
                        },
                        {
                            $type: "OpenSmc.Data.EntityReference",
                            collection: "OpenSmc.Layout.UiControl",
                            id: "LineOfBusiness/DisplayNameEditor"
                        },
                        {
                            $type: "OpenSmc.Data.EntityReference",
                            collection: "OpenSmc.Layout.UiControl",
                            id: "LineOfBusiness/Currencies"
                        },
                    ]
                }
            },
            "LineOfBusiness/DisplayName": {
                $type: "OpenSmc.Layout.HtmlControl",
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                }
            },
            "LineOfBusiness/DisplayNameEditor": {
                $type: "OpenSmc.Layout.TextBoxControl",
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
                    path: "$.DisplayName"
                }
            },
            "LineOfBusiness/Currencies": {
                $type: "OpenSmc.Layout.ItemTemplateControl",
                data: {
                    $type: "OpenSmc.Layout.DataBinding.Binding",
                    path: "$.Currencies"
                },
                view: {
                    $type: "OpenSmc.Layout.HtmlControl",
                    data: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
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