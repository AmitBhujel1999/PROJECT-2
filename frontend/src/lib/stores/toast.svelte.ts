export type ToastKind = 'success' | 'error' | 'warning' | 'info';

export interface Toast {
	id: number;
	kind: ToastKind;
	message: string;
}

class ToastState {
	items = $state<Toast[]>([]);
	#next = 1;

	push(kind: ToastKind, message: string, timeout = kind === 'error' ? 7000 : 4500) {
		const id = this.#next++;
		this.items.push({ id, kind, message });
		if (timeout) setTimeout(() => this.dismiss(id), timeout);
		return id;
	}

	success = (m: string) => this.push('success', m);
	error = (m: string) => this.push('error', m);
	warning = (m: string) => this.push('warning', m);
	info = (m: string) => this.push('info', m);

	dismiss(id: number) {
		this.items = this.items.filter((t) => t.id !== id);
	}
}

export const toast = new ToastState();

export function errorMessage(err: unknown): string {
	if (err && typeof err === 'object' && 'message' in err) return String((err as Error).message);
	return 'Something went wrong.';
}
