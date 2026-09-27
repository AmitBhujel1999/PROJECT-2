// Validated categorical slots (dataviz reference palette, light mode), fixed order.
export const SERIES = ['#2a78d6', '#eb6834', '#1baf7a'] as const;
export const GRID = '#e7e6e2';
export const AXIS = '#6b6a66';

export const axisNumber = (v: string | number) => {
	const n = Number(v);
	if (Math.abs(n) >= 1e7) return (n / 1e7).toFixed(1) + ' Cr';
	if (Math.abs(n) >= 1e5) return (n / 1e5).toFixed(1) + ' L';
	if (Math.abs(n) >= 1e3) return (n / 1e3).toFixed(0) + 'k';
	return String(n);
};
