import { defineConfig } from 'vite';
import { viteStaticCopy } from 'vite-plugin-static-copy';

export default defineConfig({
    plugins: [
        viteStaticCopy({
            targets: [
                {
                    src: 'node_modules/ag-grid-community/styles/*.min.css',
                    dest: './css/'
                }
            ],
            structured: true
        })
    ],
    build: {
        emptyOutDir: true,
        minify: false,
        rollupOptions: {
            output: {
                manualChunks: function (id) {
                    if (id.includes('node_modules')) {
                        return 'vendor';
                    }
                },
                entryFileNames: 'index.mjs',
                format: 'es'
            }
        },
        lib: {
            entry: 'index.ts',
            fileName: 'index',
            formats: ['es']
        },
        outDir: '../wwwroot'
    },
})