import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const api = 'http://localhost:5080'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': api,
      '/agent': api,
      '/hubs': { target: api, ws: true },
    },
  },
  build: {
    outDir: '../../src/SentinelDesk.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
})
