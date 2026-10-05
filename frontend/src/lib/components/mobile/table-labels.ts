/**
 * On phones every `.table-base` is shown as stacked cards by CSS. Each cell
 * needs its column name, so this attachment copies the header text into a
 * `data-label` attribute on every cell, and keeps doing so as rows change.
 */
export function tableLabels(root: HTMLElement) {
	const label = () => {
		for (const table of root.querySelectorAll<HTMLTableElement>('table.table-base')) {
			const heads = [...table.querySelectorAll('thead th')].map((th) => th.textContent?.trim() ?? '');
			for (const row of table.querySelectorAll<HTMLTableRowElement>('tbody tr, tfoot tr')) {
				let col = 0;
				for (const cell of row.cells) {
					const text = cell.colSpan > 1 ? '' : (heads[col] ?? '');
					if (cell.dataset.label !== text) cell.dataset.label = text;
					col += cell.colSpan;
				}
			}
		}
	};
	let queued = false;
	const observer = new MutationObserver(() => {
		if (queued) return;
		queued = true;
		requestAnimationFrame(() => {
			queued = false;
			label();
		});
	});
	label();
	observer.observe(root, { childList: true, subtree: true });
	return () => observer.disconnect();
}
