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
            fileName: 'index',
            formats: ['es']
        },
        outDir: '../wwwroot'
    },
})