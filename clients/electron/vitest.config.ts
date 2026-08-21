import { resolve } from 'node:path'
import { defineConfig } from 'vitest/config'

// The store, the projection and the sparkline arithmetic are plain functions over plain data,
// so they run in Node with no DOM, no Electron and no renderer. That is a property worth
// keeping: the moment these need a browser to test, they have grown a view dependency.
export default defineConfig({
  resolve: {
    alias: {
      '@shared': resolve('src/shared'),
      '@': resolve('src/renderer/src'),
    },
  },
  test: {
    environment: 'node',
    include: ['test/**/*.test.ts'],
  },
})
