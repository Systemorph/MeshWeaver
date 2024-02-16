import ReactDOM from 'react-dom/client'
import { PlaygroundPage } from "./PlaygroundPage";
import { registerControlResolver } from "@open-smc/application/src/renderControl";
import './index.scss';
import { applicationControlsResolver } from "@open-smc/application/src/applicationControlResolver";

registerControlResolver(applicationControlsResolver);

ReactDOM.createRoot(document.getElementById('root')!).render(
    <PlaygroundPage/>
)

