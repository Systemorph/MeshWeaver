import { createRoot } from 'react-dom/client'
import { SamplesPage } from "./SamplesPage.tsx";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import './index.scss';
import { registerControlResolver } from "@open-smc/app/src/controlRegistry";
import { applicationControlsResolver } from "@open-smc/app/src/applicationControlResolver";

registerControlResolver(applicationControlsResolver);

createRoot(document.getElementById('root')!).render(
    <BrowserRouter>
        <Routes>
            <Route path={"*"} element={<SamplesPage/>} />
        </Routes>
    </BrowserRouter>
);