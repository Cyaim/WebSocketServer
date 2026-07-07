<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { base } from '$app/paths';

	// Redirect to dashboard overview / 跳转到看板概览
	onMount(() => {
		goto(`${base}/overview`, { replaceState: true });
	});
</script>

<div class="flex items-center justify-center min-h-screen">
	<div class="text-center">
		<p class="text-gray-600">Redirecting to dashboard...</p>
	</div>
</div>
