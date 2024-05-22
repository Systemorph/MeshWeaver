import { createRoot } from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import { AppPage } from "@open-smc/app/src/AppPage.tsx";
import "./index.scss";

createRoot(document.getElementById('root')!).render(
    <BrowserRouter>
        <Routes>
            <Route path={"*"} element={<AppPage/>} />
        </Routes>
    </BrowserRouter>
);