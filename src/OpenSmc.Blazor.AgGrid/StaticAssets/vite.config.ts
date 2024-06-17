import { defineConfig } from 'vite';

export default defineConfig({
    build: {
        emptyOutDir: true,
        minify: false,
        rollupOptions: {
            output: {
                manualChunks: function (id) {
                    if (id.includes('node_modules')) {
                        return 'vendor';
                    }
                }
            }
        },
        lib: {
            entry: 'index.ts',
            fileName: 'ag-grid',
            formats: ['es']
        },
        outDir: '../wwwroot'
    },
})