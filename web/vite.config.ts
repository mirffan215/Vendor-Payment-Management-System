/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const api = process.env.VPMS_API ?? 'https://localhost:7030'

export default defineConfig({
  plugins: [react()],
  server: {
    // The browser only ever talks to this origin; the dev server forwards API and sign-in calls (cookie sessions).
    proxy: {
      '/api': { target: api, secure: false },
      '/auth': { target: api, secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
