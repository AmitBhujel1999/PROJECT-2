/**
 * Thin fetch wrapper for the Django REST API.
 *
 * - Same-origin requests with the HTTP-only session cookie (never localStorage).
 * - CSRF token read from the (non-HttpOnly) csrf cookie and sent as X-CSRFToken.
 * - Unwraps the {success, data, message} envelope and throws ApiError otherwise.
 */

export class ApiError extends Error {
	constructor(
		public status: number,
		public code: string,
		message: string,
		public details?: unknown
	) {
		super(message);
	}
}

export interface Envelope<T> {
	success: boolean;
	data: T;
	message: string;
}

const CSRF_COOKIE = 'acct_csrftoken';
const UNSAFE = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

let unauthorizedHandler: (() => void) | null = null;
export function onUnauthorized(handler: () => void) {
	unauthorizedHandler = handler;
}

function readCookie(name: string): string | null {
	if (typeof document === 'undefined') return null;
	const match = document.cookie.split('; ').find((c) => c.startsWith(`${name}=`));
	return match ? decodeURIComponent(match.split('=')[1]) : null;
}

export async function ensureCsrf(): Promise<void> {
	if (!readCookie(CSRF_COOKIE)) {
		await fetch('/api/auth/csrf/', { credentials: 'same-origin' });
	}
}

export type Query = Record<string, string | number | boolean | null | undefined>;

export function buildUrl(path: string, query?: Query): string {
	const url = path.startsWith('/api/') ? path : `/api/${path.replace(/^\//, '')}`;
	if (!query) return url;
	const params = new URLSearchParams();
	for (const [k, v] of Object.entries(query)) {
		if (v === undefined || v === null || v === '') continue;
		params.set(k, String(v));
	}
	const qs = params.toString();
	return qs ? `${url}?${qs}` : url;
}

export async function request<T>(
	method: string,
	path: string,
	options: { query?: Query; body?: unknown; signal?: AbortSignal } = {}
): Promise<Envelope<T>> {
	const headers: Record<string, string> = { Accept: 'application/json' };
	if (UNSAFE.has(method)) {
		await ensureCsrf();
		headers['X-CSRFToken'] = readCookie(CSRF_COOKIE) ?? '';
		headers['Content-Type'] = 'application/json';
	}
	let response: Response;
	try {
		response = await fetch(buildUrl(path, options.query), {
			method,
			headers,
			credentials: 'same-origin',
			body: options.body === undefined ? undefined : JSON.stringify(options.body),
			signal: options.signal
		});
	} catch (err) {
		if ((err as Error).name === 'AbortError') throw err;
		throw new ApiError(0, 'NETWORK_ERROR', 'Cannot reach the server. Check your connection and try again.');
	}
	let payload: any = null;
	const text = await response.text();
	if (text) {
		try {
			payload = JSON.parse(text);
		} catch {
			payload = null;
		}
	}
	if (!response.ok || !payload?.success) {
		const error = payload?.error ?? {};
		if (response.status === 401 && unauthorizedHandler) unauthorizedHandler();
		throw new ApiError(
			response.status,
			error.code ?? 'ERROR',
			error.message ?? `Request failed (${response.status}).`,
			error.details
		);
	}
	return payload as Envelope<T>;
}

export const api = {
	get: <T>(path: string, query?: Query, signal?: AbortSignal) =>
		request<T>('GET', path, { query, signal }).then((r) => r.data),
	post: <T>(path: string, body?: unknown) => request<T>('POST', path, { body: body ?? {} }),
	put: <T>(path: string, body?: unknown) => request<T>('PUT', path, { body }),
	patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, { body }),
	delete: <T>(path: string) => request<T>('DELETE', path)
};

/** URL for a file download (CSV/PDF) that reuses the current filters. */
export function exportUrl(path: string, query: Query, kind: 'csv' | 'pdf', inline = false): string {
	return buildUrl(path, { ...query, page: undefined, page_size: undefined, export: kind, inline: inline ? 1 : undefined });
}

/** Map DRF field errors ({field: [msg]}) to {field: msg}. */
export function fieldErrors(err: unknown): Record<string, string> {
	if (!(err instanceof ApiError) || !err.details || typeof err.details !== 'object') return {};
	const out: Record<string, string> = {};
	for (const [key, value] of Object.entries(err.details as Record<string, unknown>)) {
		out[key] = Array.isArray(value) ? String(value[0]) : typeof value === 'string' ? value : JSON.stringify(value);
	}
	return out;
}
