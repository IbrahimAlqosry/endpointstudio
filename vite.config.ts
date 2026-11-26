import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The React UI talks to the local .NET helper service (server/) through /api.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5199', changeOrigin: true },
    },
  },
  build: {
    // The .NET server serves the built UI, so `npm run build` + `npm start` runs everything from one URL.
    outDir: 'server/wwwroot',
    emptyOutDir: true,
  },
})
