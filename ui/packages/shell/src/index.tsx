import { registerControlResolver } from "@open-smc/application/src/renderControl.tsx";
import { applicationControlsResolver } from "@open-smc/application/src/applicationControlResolver.ts";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import { AppPage } from "@open-smc/application/src/AppPage.tsx";
import "./index.scss";

registerControlResolver(applicationControlsResolver);

createRoot(document.getElementById('root')!).render(
    <BrowserRouter>
        <Routes>
            <Route path={"*"} element={<AppPage/>} />
        </Routes>
    </BrowserRouter>
);