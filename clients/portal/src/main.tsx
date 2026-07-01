import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { Portal } from "./Portal";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Portal />
  </StrictMode>,
);
