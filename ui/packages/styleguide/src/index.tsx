import { createRoot } from "react-dom/client";
import { MenuItemPage } from "./MenuItemPage";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import React from "react";
import { MainLayout } from "./MainLayout";
import { HomePage } from "./HomePage";
import "./index.scss";
import { MultiselectPage } from "./MultiselectPage";
import { ClassificationPage } from "./ClassificationPage";
import { GridPage } from "./GridPage";
import { TitlePage } from "./TitlePage";
import { BadgePage } from "./BadgePage";
import { ItemTemplatePage } from "./ItemTemplatePage";
import { MainWindowPage } from "./MainWindowPage";
import { ChartPage } from "./ChartPage";
import { TextboxPage } from "./TextboxPage";
import { NumberPage } from "./NumberPage";
import { ActivityPage } from "./ActivityPage";
import { HtmlPage } from "./HtmlPage";
import { IconPage } from "./IconPage";
import { CheckboxPage } from "./CheckboxPage";
import { SpinnerPage } from "./SpinnerPage";
import { ProgressPage } from "./ProgressPage";
import { GridLayoutPage } from "./GridLayoutPage";
import {LayoutPage} from "./LayoutPage";
import { ModalPage } from "./ModalPage";
import { NotebookEditorPage } from "./NotebookEditorPage";
import { registerControlResolver } from "@open-smc/application/renderControl";
import { portalControlsResolver } from "@open-smc/portal/portalControlResolver";
import { applicationControlsResolver } from "@open-smc/application/applicationControlResolver";

registerControlResolver(applicationControlsResolver);
registerControlResolver(portalControlsResolver);

const container = document.getElementById('root');
const root = createRoot(container!);

root.render(
    <BrowserRouter>
        <Routes>
            <Route path="/smapp" element={<MainWindowPage/>}/>
            <Route path="/" element={<MainLayout/>}>
                <Route index element={<HomePage/>}/>
                <Route path="notebook-editor" element={<NotebookEditorPage/>}/>
                <Route path="menu-item" element={<MenuItemPage/>}/>
                <Route path="multiselect" element={<MultiselectPage/>}/>
                <Route path="classification" element={<ClassificationPage/>}/>
                <Route path="grid" element={<GridPage/>}/>
                <Route path="title" element={<TitlePage/>}/>
                <Route path="textbox" element={<TextboxPage/>}/>
                <Route path="number" element={<NumberPage/>}/>
                <Route path="badge" element={<BadgePage/>}/>
                <Route path="item-template" element={<ItemTemplatePage/>}/>
                <Route path="main-window" element={<MainWindowPage/>}/>
                <Route path="chart" element={<ChartPage/>}/>
                <Route path="activity" element={<ActivityPage/>}/>
                <Route path="html" element={<HtmlPage/>}/>
                <Route path="icon" element={<IconPage/>}/>
                <Route path="checkbox" element={<CheckboxPage/>}/>
                <Route path="spinner" element={<SpinnerPage/>}/>
                <Route path="progress" element={<ProgressPage/>}/>
                <Route path="grid-layout" element={<GridLayoutPage/>}/>
                <Route path="layout" element={<LayoutPage/>}/>
                <Route path="modal" element={<ModalPage/>}/>
            </Route>
        </Routes>
    </BrowserRouter>
);