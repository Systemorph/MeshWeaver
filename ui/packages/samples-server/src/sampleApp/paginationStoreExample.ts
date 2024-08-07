const paginationStoreExample = {
    $type: "MeshWeaver.Data.EntityStore",
    reference: {
        $type: "MeshWeaver.Layout.LayoutAreaReference",
        area: "Main",
        options: {
            orderBy: "DisplayName",
            page: 1,
            pageSize: 10
        }
    },
    instances: {
        $areas: {
            "Main": {
                $type: "ItemTemplateControl",
                dataContext: {
                    $type: "CollectionReference",
                    collection: "LineOfBusiness"
                },
                data: {
                    $type: "Binding",
                    path: "$"
                },
                view: {
                    $type: "LayoutStackControl",
                    areas: [
                        {
                            $type: "EntityReference",
                            collection: "$areas",
                            id: "Main/SystemName"
                        }
                    ]
                }
            },
            "Main/SystemName": {
                $type: "TextBoxControl",
                data: {
                    $type: "Binding",
                    path: "$.SystemName"
                }
            }
        },
        LineOfBusiness: {
            "1": {
                SystemName: "1",
                DisplayName: "1"
            },
            "2": {
                SystemName: "2",
                DisplayName: "2"
            }
        }
    }
}