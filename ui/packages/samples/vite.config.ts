import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { samplesServerPlugin } from "../samples-server/dist/samplesServerPlugin.mjs";

// https://vitejs.dev/config/
export default defineConfig({
    plugins: [
        react(),
        samplesServerPlugin()
    ],
});