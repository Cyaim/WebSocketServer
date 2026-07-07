import { mdsvex } from 'mdsvex';
import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	// Consult https://svelte.dev/docs/kit/integrations
	// for more information about preprocessors
	preprocess: [vitePreprocess(), mdsvex()],
	kit: {
		// SPA build served by the backend DashboardMiddleware at /dashboard.
		// 以 SPA 形式构建，由后端 DashboardMiddleware 挂在 /dashboard 下提供服务。
		adapter: adapter({ fallback: 'index.html' }),
		paths: { base: '/dashboard' }
	},
	extensions: ['.svelte', '.svx']
};

export default config;
