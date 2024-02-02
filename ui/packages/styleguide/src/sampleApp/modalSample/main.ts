import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { makeCheckbox } from "@open-smc/sandbox/src/Checkbox";
import { makeHtml } from "@open-smc/sandbox/src/Html";
import { makeIcon } from "@open-smc/sandbox/src/Icon";
import { makeItemTemplate } from "@open-smc/sandbox/src/ItemTemplate";
import { makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { v4 } from "uuid";

export const main = makeStack()
    .withSkin("HorizontalPanel")
    .withStyle(style=>style.withGap("24px").withAlignItems("flex-start"))
    .withView(
        makeIcon("logs")
            .withColor("#874170")
            .withSize("XL")
            .withStyle(style=> style.withMargin("10px 0 0"))
    )
    .withView(
        makeStack()
            .withView(
                makeHtml(
                    `
                    <p>Lorem ipsum dolor sit amet, consectetur adipiscing elit. Vivamus tristique ex ut nulla consequat, sit amet feugiat est rutrum. Sed porttitor blandit ipsum id tincidunt. Sed dapibus velit ut diam malesuada facilisis. Ut ultricies leo sit amet nulla vestibulum, vel accumsan magna auctor. Sed malesuada tincidunt ex, quis euismod quam euismod rhoncus. Nulla consequat, velit non porttitor tempus, nibh eros consequat elit, viverra fringilla orci libero sit amet ipsum. Aliquam et blandit mauris. Interdum et malesuada fames ac ante ipsum primis in faucibus. Aliquam erat volutpat. Vivamus nec varius nisi. Cras a sem metus. Aenean id porttitor massa. Nam id leo ac sem venenatis volutpat.</p>
                    <p>Mauris luctus laoreet enim, non dictum eros luctus ac. Vestibulum eget interdum nibh, et rutrum enim. Curabitur consectetur lectus enim, a hendrerit augue malesuada sed. Curabitur rhoncus viverra mi, non ultricies odio gravida ut. Proin vulputate felis ullamcorper eros vehicula tempor. Donec faucibus consequat tortor vel blandit. Proin bibendum augue ac augue porttitor, et pretium leo rhoncus. Praesent maximus nisl rutrum nibh laoreet, ac hendrerit enim volutpat. Maecenas bibendum a ex a eleifend. Maecenas fringilla, augue quis dictum porta, purus eros ultrices nulla, ac pretium magna ante quis ligula. Curabitur placerat orci sem, eu dapibus elit pretium non. Etiam venenatis lorem et magna interdum ornare. Donec erat turpis, auctor ut leo eu, mattis semper purus. Proin ornare nibh non est convallis egestas egestas id est.</p>
                    <p>Etiam bibendum nisl ac augue blandit, vel luctus quam volutpat. Etiam scelerisque nibh est, nec lacinia sapien dignissim vel. Sed aliquam efficitur turpis, vel vulputate magna tempus et. Proin nisi felis, tristique id tempus quis, ultricies vitae velit. Quisque eu luctus ex. Nulla malesuada mi eget nulla rutrum rutrum. Aenean vitae urna at magna semper elementum. Duis tempor consequat sem eu aliquet. Integer in elementum purus.</p>
                    <p>Vivamus viverra pulvinar velit id porttitor. In hac habitasse platea dictumst. Donec a placerat enim. In hac habitasse platea dictumst. Integer sed molestie metus. Nam lacinia, ligula ut vehicula vestibulum, diam felis congue ligula, non sagittis purus lectus quis neque. Proin eleifend et odio luctus semper. Duis et varius eros. Aenean non erat quis tellus ornare feugiat. Mauris nec nibh erat. Duis luctus quam lorem, ut fermentum risus sagittis non. Duis non rutrum purus, at luctus lorem.</p>
                    `)
            )
            .withFlex(style=>style.withGap('24px'))
            .withSkin("VerticalPanel")
            .withView(
                makeItemTemplate()
                    .withView(
                        makeCheckbox()
                            .withId(makeBinding("item.id"))
                            .withLabel(makeBinding("item.label"))
                            .withData(makeBinding("item.data"))
                            .isReadOnly(makeBinding("item.isReadOnly"))
                            .build()
                    )
                    .withFlex(style=>style.withGap('12px'))
                    .withSkin("VerticalPanel")
                    .withData(
                        [
                            {
                                id: v4(),
                                data: false,
                                label: "Lorem ipsum dolor sit amet, consectetur adipiscing elit",
                                isReadOnly: false,
                            },
                            {
                                id: v4(),
                                data: false,
                                label: "Nam egestas dolor sed porttitor aliquam",
                                isReadOnly: false,
                            },
                            {
                                id: v4(),
                                data: true,
                                label: "Lorem ipsum dolor sit amet, consectetur adipiscing elit",
                                isReadOnly: false,
                            },
                            {
                                id: v4(),
                                data: false,
                                label: "Nam in magna leo",
                                isReadOnly: false,
                            },
                        ]
                    )
            )
    );
