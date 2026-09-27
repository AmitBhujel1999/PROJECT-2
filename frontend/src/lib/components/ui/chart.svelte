<script lang="ts">
	import {
		Chart,
		BarController,
		BarElement,
		LineController,
		LineElement,
		PointElement,
		CategoryScale,
		LinearScale,
		Tooltip,
		Legend,
		Filler,
		type ChartConfiguration
	} from 'chart.js';

	Chart.register(BarController, BarElement, LineController, LineElement, PointElement, CategoryScale, LinearScale, Tooltip, Legend, Filler);

	let { config, height = 260, label }: { config: ChartConfiguration; height?: number; label: string } = $props();

	let canvas: HTMLCanvasElement;
	let chart: Chart | null = null;

	$effect(() => {
		const cfg = $state.snapshot(config) as ChartConfiguration;
		chart?.destroy();
		chart = new Chart(canvas, {
			...cfg,
			options: {
				responsive: true,
				maintainAspectRatio: false,
				animation: false,
				interaction: { mode: 'index', intersect: false },
				...cfg.options,
				plugins: {
					legend: { position: 'top', align: 'end', labels: { boxWidth: 10, boxHeight: 10, color: '#52514e', font: { size: 11 } } },
					tooltip: { backgroundColor: '#0b0b0b', padding: 8, titleFont: { size: 11 }, bodyFont: { size: 12 } },
					...cfg.options?.plugins
				}
			}
		});
		return () => {
			chart?.destroy();
			chart = null;
		};
	});
</script>

<div style="height: {height}px" class="relative">
	<canvas bind:this={canvas} aria-label={label}>{label}</canvas>
</div>
