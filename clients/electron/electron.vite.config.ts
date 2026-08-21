import { resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import tailwind from '@tailwindcss/vite'
import { defineConfig, externalizeDepsPlugin } from 'electron-vite'

// Three builds from one config, because an Electron app is three programs: a Node process,
// a bridge script, and a browser page. They do not share a global environment, and keeping
// their builds separate is what stops renderer code from quietly reaching for Node APIs.
export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    resolve: { alias: { '@shared': resolve('src/shared') } },
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    resolve: { alias: { '@shared': resolve('src/shared') } },
  },
  renderer: {
    resolve: {
      alias: {
        '@shared': resolve('src/shared'),
        '@': resolve('src/renderer/src'),
      },
    },
    plugins: [react(), tailwind()],
    build: {
      // electron-vite leaves the renderer unminified by default. Startup time is one of the
      // numbers this client exists to contribute to a framework comparison, and parsing
      // several hundred kilobytes of unminified JavaScript on every launch would make that
      // number say more about the build config than about the framework.
      minify: 'esbuild',
    },
  },
})
