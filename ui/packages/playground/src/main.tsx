import { createRoot } from 'react-dom/client'
import { PlaygroundPage } from "./PlaygroundPage";
import { registerControlResolver } from "@open-smc/application/src/renderControl";
import { applicationControlsResolver } from "@open-smc/application/src/applicationControlResolver";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import './index.scss';

registerControlResolver(applicationControlsResolver);

createRoot(document.getElementById('root')!).render(
    <BrowserRouter>
        <Routes>
            <Route path={"*"} element={<PlaygroundPage/>} />
        </Routes>
    </BrowserRouter>
)

