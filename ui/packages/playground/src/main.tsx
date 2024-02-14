import ReactDOM from 'react-dom/client'
import { PlaygroundPage } from "./PlaygroundPage";
import { registerControlResolver } from "@open-smc/application/src/renderControl.tsx";
import './index.scss';

registerControlResolver(name => {
    switch (name) {
        case "MenuItemControl":
            return import("@open-smc/application/src/controls/MenuItemControl");
        default:
            throw 1;
    }
});

ReactDOM.createRoot(document.getElementById('root')!).render(
    <PlaygroundPage/>
)

