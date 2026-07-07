import { paraglideVitePlugin } from '@inlang/paraglide-js';
import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
	plugins: [
		tailwindcss(),
		sveltekit(),
		paraglideVitePlugin({
			project: './project.inlang',
			outdir: './src/lib/paraglide'
		})
	],
	server: {
		proxy: {
			// Dashboard controllers live under /ws_server/api on the backend.
			// 后端 Dashboard 控制器挂在 /ws_server/api 下。
			'/ws_server': {
				target: 'http://localhost:5000', // 后端 API 地址 / Backend API address
				changeOrigin: true,
				secure: false
			}
		}
	}
});
