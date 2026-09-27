import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vitest/config';

// In development the Django API runs on :8000; the dev server proxies /api so
// the browser talks to a single origin (session + CSRF cookies just work).
const API_TARGET = process.env.API_PROXY_TARGET ?? 'http://127.0.0.1:8000';

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	server: {
		port: 5173,
		proxy: {
			'/api': { target: API_TARGET, changeOrigin: false },
			'/admin': { target: API_TARGET, changeOrigin: false },
			'/static': { target: API_TARGET, changeOrigin: false }
		}
	},
	// `bun run preview` serves the production build; used by start-windows.ps1
	// (no Docker/nginx), so it forwards the same paths nginx would.
	preview: {
		port: 4173,
		proxy: {
			'/api': { target: API_TARGET, changeOrigin: false },
			'/admin': { target: API_TARGET, changeOrigin: false },
			'/static': { target: API_TARGET, changeOrigin: false }
		}
	},
	test: {
		include: ['src/**/*.test.ts'],
		environment: 'node'
	}
});
