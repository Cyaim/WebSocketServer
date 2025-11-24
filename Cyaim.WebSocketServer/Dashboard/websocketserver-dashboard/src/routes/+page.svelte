<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	
	// Redirect to dashboard overview
	onMount(() => {
		goto('/dashboard/overview');
	});
</script>

<div class="flex items-center justify-center min-h-screen">
	<div class="text-center">
		<p class="text-gray-600">Redirecting to dashboard...</p>
	</div>
</div>
