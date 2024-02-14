import ReactDOM from 'react-dom/client'
import { PlaygroundPage } from "./PlaygroundPage";
import { registerControlResolver } from "@open-smc/application/src/renderControl";
import './index.scss';

registerControlResolver(name => {
    switch (name) {
        case "MenuItemControl":
            return import("@open-smc/application/src/controls/MenuItemControl");
        case "LayoutStackControl":
            return import("@open-smc/application/src/controls/LayoutStackControl");
        default:
            throw `Unknown control ${name}`;
    }
});

ReactDOM.createRoot(document.getElementById('root')!).render(
    <PlaygroundPage/>
)

