<script lang="ts">
	import { page } from '$app/state';
	import { base } from '$app/paths';
	import { m } from '$lib/paraglide/messages';

	let { children } = $props();

	// Route paths are base-relative; `base` (/dashboard) is prepended for URLs.
	// 路由为相对 base 的路径；URL 前会拼上 base（/dashboard）。
	const navItems = [
		{ path: `${base}/overview`, label: m.dashboard_nav_overview(), icon: '📊' },
		{ path: `${base}/nodes`, label: m.dashboard_nav_nodes(), icon: '🖥️' },
		{ path: `${base}/clients`, label: m.dashboard_nav_clients(), icon: '👥' },
		{ path: `${base}/bandwidth`, label: m.dashboard_nav_bandwidth(), icon: '📈' },
		{ path: `${base}/dataflow`, label: m.dashboard_nav_dataflow(), icon: '🔄' },
		{ path: `${base}/send`, label: m.dashboard_nav_send(), icon: '📤' }
	];
</script>

<div class="min-h-screen bg-gray-50">
	<!-- Header -->
	<header class="bg-gradient-to-r from-purple-600 to-indigo-600 text-white shadow-lg">
		<div class="container mx-auto px-4 py-4">
			<h1 class="text-2xl font-bold mb-4">🚀 {m.dashboard_title()}</h1>
			
			<!-- Navigation -->
			<nav class="flex flex-wrap gap-2">
				{#each navItems as item}
					<a
						href={item.path}
						class="px-4 py-2 rounded-lg transition-all duration-200 {page.url.pathname === item.path
							? 'bg-white text-purple-600 font-semibold'
							: 'bg-white/20 hover:bg-white/30 text-white'}"
					>
						<span class="mr-2">{item.icon}</span>
						{item.label}
					</a>
				{/each}
			</nav>
		</div>
	</header>

	<!-- Main Content -->
	<main class="container mx-auto px-4 py-6 max-w-7xl">
		{@render children()}
	</main>
</div>

<style>
	:global(body) {
		margin: 0;
		font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu,
			Cantarell, 'Open Sans', 'Helvetica Neue', sans-serif;
	}
</style>

