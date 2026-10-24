/**
 * Online copy ("Phone anywhere"). When the screens are opened from the Firebase site
 * (mero-lekhapal.web.app) instead of the PC, API reads are answered from the copy the PC
 * program uploads: one Firestore document per page, gzip + base64, in one or more parts.
 * Lists hold every row, so searching, filtering, sorting and paging happen here.
 * Sales and receipts can be made on the phone (entry.ts); everything else needs the PC.
 */
import { ApiError, type Envelope, type Query } from '$lib/api/client';
import { calculateSale, createReceipt, createSale, openInvoices, pendingById, pendingList, refreshPending } from './entry';

export const FIREBASE = {
	apiKey: 'AIzaSyBab1tS3IgJeMkPzym9NOLzVVuqTw3hLLc',
	projectId: 'mero-lekhapal'
};

const STORE = 'ml-cloud';
const DOCS = `https://firestore.googleapis.com/v1/projects/${FIREBASE.projectId}/databases/(default)/documents`;

export interface CloudLink {
	book: string;
	uid: string;
	refresh: string;
}

/** True when these screens run from the Firebase site (not from the PC program). */
export const cloudMode =
	typeof location !== 'undefined' && (location.hostname.endsWith('.web.app') || location.hostname.endsWith('.firebaseapp.com'));

export function cloudLink(): CloudLink | null {
	try {
		const raw = localStorage.getItem(STORE);
		return raw ? (JSON.parse(raw) as CloudLink) : null;
	} catch {
		return null;
	}
}

export function saveLink(link: CloudLink | null) {
	try {
		if (link) localStorage.setItem(STORE, JSON.stringify(link));
		else localStorage.removeItem(STORE);
	} catch {
		// Private mode: the link lasts for this visit only.
	}
	token = null;
	book = null;
	pages.clear();
}

// ---------------------------------------------------------------- Firebase sign-in

let token: { id: string; expires: number } | null = null;

/** A new anonymous Firebase account for this phone. */
export async function signUp(): Promise<{ uid: string; refresh: string; id: string }> {
	const r = await fetch(`https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=${FIREBASE.apiKey}`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ returnSecureToken: true })
	});
	if (!r.ok) throw new ApiError(r.status, 'CLOUD_ERROR', 'Could not reach Google. Check the internet connection.');
	const j = await r.json();
	token = { id: j.idToken, expires: Date.now() + (Number(j.expiresIn) - 120) * 1000 };
	return { uid: j.localId, refresh: j.refreshToken, id: j.idToken };
}

export const firestoreBase = () => DOCS;
export const currentToken = () => idToken();

async function idToken(): Promise<string> {
	if (token && token.expires > Date.now()) return token.id;
	const link = cloudLink();
	if (!link) throw new ApiError(401, 'NOT_LINKED', 'This phone is not linked yet.');
	let r: Response;
	try {
		r = await fetch(`https://securetoken.googleapis.com/v1/token?key=${FIREBASE.apiKey}`, {
			method: 'POST',
			headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
			body: `grant_type=refresh_token&refresh_token=${encodeURIComponent(link.refresh)}`
		});
	} catch {
		throw new ApiError(0, 'NETWORK_ERROR', 'No internet connection.');
	}
	if (!r.ok) throw new ApiError(401, 'NOT_LINKED', 'This phone needs to be linked again.');
	const j = await r.json();
	token = { id: j.id_token, expires: Date.now() + (Number(j.expires_in) - 120) * 1000 };
	if (j.refresh_token !== link.refresh) saveLinkKeepingToken({ ...link, refresh: j.refresh_token });
	return token.id;
}

function saveLinkKeepingToken(link: CloudLink) {
	try {
		localStorage.setItem(STORE, JSON.stringify(link));
	} catch {
		// ignore
	}
}

// ---------------------------------------------------------------- Firestore reads

type Fields = Record<string, any>;

export const firestore = (path: string) => getDoc(path);

async function getDoc(path: string): Promise<Fields | null> {
	let r: Response;
	try {
		r = await fetch(`${DOCS}/${path}`, { headers: { Authorization: `Bearer ${await idToken()}` } });
	} catch (e) {
		if (e instanceof ApiError) throw e;
		throw new ApiError(0, 'NETWORK_ERROR', 'No internet connection.');
	}
	if (r.status === 404 || r.status === 403) return null;
	if (!r.ok) throw new ApiError(r.status, 'CLOUD_ERROR', `The online copy answered ${r.status}.`);
	return (await r.json()).fields ?? {};
}

export interface Book {
	name: string;
	updated: string;
	me: any;
	/** This phone's bill series ("M1") and the next free numbers; whether phones may make sales. */
	prefix: string;
	nextSale: number;
	nextReceipt: number;
	entry: boolean;
}

let book: Book | null = null;

/** The linked company as last loaded (no network). */
export const currentBookCache = () => book;

/** The linked company: name, last upload time and this phone's user (null when no longer allowed). */
export async function loadBook(force = false): Promise<Book | null> {
	if (book && !force) return book;
	const link = cloudLink();
	if (!link) return null;
	const f = await getDoc(`books/${link.book}`);
	const member = f?.members?.mapValue?.fields?.[link.uid]?.mapValue?.fields;
	if (!f || !member) return (book = null);
	book = {
		name: f.name?.stringValue ?? '',
		updated: f.updated?.timestampValue ?? '',
		me: JSON.parse(member.me?.stringValue ?? 'null'),
		prefix: member.prefix?.stringValue ?? '',
		nextSale: Number(member.next_sale?.integerValue ?? 1),
		nextReceipt: Number(member.next_receipt?.integerValue ?? 1),
		entry: f.entry?.booleanValue !== false
	};
	return book;
}

async function gunzip(base64: string): Promise<string> {
	const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
	if (typeof DecompressionStream === 'undefined')
		throw new ApiError(0, 'OLD_BROWSER', 'This phone is too old for the online copy. Update iOS/Android or the browser.');
	const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
	return await new Response(stream).text();
}

const pages = new Map<string, Promise<any>>();

/** The data of one page, e.g. "sales/12/"; null when it is not in the online copy. */
async function page(path: string): Promise<any> {
	const link = cloudLink()!;
	const id = path.replace(/^\/+|\/+$/g, '').replaceAll('/', '~');
	const first = await getDoc(`books/${link.book}/views/${id}`);
	if (!first) return null;
	const parts = Number(first.parts?.integerValue ?? 1);
	const rest = await Promise.all(
		Array.from({ length: parts - 1 }, (_, i) => getDoc(`books/${link.book}/views/${id}~~${i + 2}`))
	);
	const text = [first, ...rest].map((d) => d?.z?.stringValue ?? '').join('');
	return JSON.parse(await gunzip(text));
}

export function cachedPage(path: string): Promise<any> {
	let p = pages.get(path);
	if (!p) {
		p = page(path);
		pages.set(path, p);
		p.catch(() => pages.delete(path));
		// Keep a page for a minute; the PC uploads at most every minute.
		setTimeout(() => pages.delete(path), 60_000);
	}
	return p;
}

// ---------------------------------------------------------------- the API, answered from the copy

const NOT_HERE = 'Not in the online copy. Open this on the computer (or on the same Wi-Fi as the computer).';
const READ_ONLY = 'Only sales and receipts can be made on the online copy. Do this on the computer, or on the same Wi-Fi as the computer.';

export async function cloudRequest<T>(method: string, rawPath: string, query: Query = {}, body: any = {}): Promise<Envelope<T>> {
	const path = (rawPath.replace(/^\/?(api\/)?/, '').split('?')[0] + '/').replace(/\/+$/, '/');
	const ok = (data: any): Envelope<T> => ({ success: true, data, message: '' });

	if (path === 'auth/csrf/') return ok(null);
	if (path === 'auth/logout/') return ok(null);

	if (!cloudLink()) throw new ApiError(401, 'NOT_LINKED', 'This phone is not linked yet.');
	const b = await loadBook();
	if (!b) throw new ApiError(401, 'NOT_LINKED', 'This phone is no longer allowed. Link it again from the computer.');

	if (method !== 'GET') {
		if (path === 'sales/calculate/') return ok(await calculateSale(body));
		if (path === 'sales/') {
			const r = await createSale(body);
			return { success: true, data: r.data as T, message: r.message };
		}
		if (path === 'customer-receipts/') {
			const r = await createReceipt(body);
			return { success: true, data: r.data as T, message: r.message };
		}
		throw new ApiError(403, 'READ_ONLY', READ_ONLY);
	}

	// Bills made on this phone that the computer has not booked yet.
	const pendingId = path.match(/^(sales|customer-receipts)\/(p-[^/]+)\/$/);
	if (pendingId) {
		await refreshPending();
		const p = pendingById(pendingId[2]);
		if (!p) throw new ApiError(404, 'NOT_FOUND', 'Not found on this phone.');
		return ok({ ...p.doc, pending_status: p.status, pending_message: p.message, booked_id: p.realId || null });
	}
	if (path === 'customer-receipts/open-documents/') return ok((await openInvoices(Number(query.party))) as T);
	if (path === 'auth/me/') return ok(b.me);
	if (path === 'companies/') return ok({ companies: [{ slug: 'main', name: b.name }], current: 'main', setup: false });
	if (path.endsWith('/pdf/') || query.export) throw new ApiError(404, 'NOT_AVAILABLE', 'PDFs and exports need the computer.');

	const data = await cachedPage(path);
	if (data === null) throw new ApiError(404, 'NOT_AVAILABLE', NOT_HERE);
	if (data && Array.isArray(data.results) && typeof data.count === 'number') {
		// Waiting bills from this phone first in the sales / receipts lists (until the copy has them).
		const kind = path === 'sales/' ? 'sale' : path === 'customer-receipts/' ? 'receipt' : null;
		if (kind) {
			await refreshPending().catch(() => []);
			const known = new Set(data.results.map((r: any) => r.invoice_number ?? r.number));
			const extra = pendingList()
				.filter((p) => p.kind === kind && !known.has(p.number))
				.map((p) => ({ ...p.row, pending_status: p.status }));
			return ok(filterList({ ...data, results: [...extra, ...data.results], count: data.count + extra.length }, query) as T);
		}
		return ok(filterList(data, query) as T);
	}
	return ok(data);
}

// ---------------------------------------------------------------- lists: filter, search, sort, page

const SKIP = new Set(['page', 'page_size', 'ordering', 'search', 'export', 'as_of', 'q']);

function value(row: any, key: string): any {
	if (key in row) return row[key];
	if (`${key}_id` in row) return row[`${key}_id`];
	return undefined;
}

function matches(row: any, key: string, want: string): boolean {
	if (want === 'ALL' || want === '') return true;
	if (/(^|_)(start_date|date_from|from)$/.test(key)) return !row.date || String(row.date) >= want;
	if (/(^|_)(end_date|date_to|to)$/.test(key)) return !row.date || String(row.date) <= want;
	const v = value(row, key);
	if (v === undefined) return true; // a filter this copy cannot apply: show everything
	if (v !== null && typeof v === 'object') return String(v.id ?? '') === want;
	return String(v) === want;
}

function text(v: any, depth = 0): string {
	if (v === null || v === undefined) return '';
	if (typeof v === 'object') return depth > 1 ? '' : Object.values(v).map((x) => text(x, depth + 1)).join(' ');
	return String(v);
}

function compare(a: any, b: any): number {
	const na = Number(a);
	const nb = Number(b);
	if (a !== '' && b !== '' && !Number.isNaN(na) && !Number.isNaN(nb)) return na - nb;
	return String(a ?? '').localeCompare(String(b ?? ''), undefined, { sensitivity: 'base' });
}

function filterList(data: any, query: Query): any {
	let rows: any[] = data.results;
	for (const [k, v] of Object.entries(query)) {
		if (SKIP.has(k) || v === undefined || v === null) continue;
		rows = rows.filter((r) => matches(r, k, String(v)));
	}
	const search = String(query.search ?? query.q ?? '').trim().toLowerCase();
	if (search) rows = rows.filter((r) => text(r).toLowerCase().includes(search));
	if (query.ordering) {
		const keys = String(query.ordering).split(',').filter(Boolean);
		rows = [...rows].sort((a, b) => {
			for (const key of keys) {
				const desc = key.startsWith('-');
				const k = desc ? key.slice(1) : key;
				const c = compare(a[k], b[k]);
				if (c) return desc ? -c : c;
			}
			return 0;
		});
	}
	const size = Math.max(1, Number(query.page_size ?? 25) || 25);
	const total = Math.max(1, Math.ceil(rows.length / size));
	const pageNo = Math.min(Math.max(1, Number(query.page ?? 1) || 1), total);
	const filtered = rows.length !== data.results.length;
	return {
		...data,
		// Totals of the whole list are only right when nothing is filtered out.
		summary: filtered ? undefined : data.summary,
		results: rows.slice((pageNo - 1) * size, pageNo * size),
		count: rows.length,
		page: pageNo,
		page_size: size,
		total_pages: total
	};
}

// ---------------------------------------------------------------- pairing (the /link page)

/** Link with the code typed from the PC screen ("K7P2-M9QX"); resolves when the PC accepted. */
export async function pairWithCode(typed: string, device: string): Promise<void> {
	const short = typed.toUpperCase().replace(/[^A-Z0-9]/g, '');
	if (short.length !== 8) throw new ApiError(400, 'BAD_CODE', 'Type the 8 letters and numbers shown on the computer.');
	const me = await signUp();
	// The PC publishes a new code within a few seconds of showing it: try for a little while.
	let r: Response | null = null;
	for (let i = 0; i < 8; i++) {
		r = await fetch(`${DOCS}/codes/${short.slice(0, 4)}-${short.slice(4)}`, { headers: { Authorization: `Bearer ${me.id}` } }).catch(() => null);
		if (!r) throw new ApiError(0, 'NETWORK_ERROR', 'No internet connection.');
		if (r.ok) break;
		await new Promise((res) => setTimeout(res, 2500));
	}
	if (!r?.ok) throw new ApiError(404, 'BAD_CODE', 'This code is wrong or has expired, or the computer is offline. Make a new code on the computer.');
	const f = (await r.json()).fields ?? {};
	await pair(f.book?.stringValue ?? '', f.code?.stringValue ?? '', device, me);
}

/** Ask to join the book with the one-time code from the PC; resolves when the PC accepted. */
export async function pair(bookId: string, code: string, device: string, account?: { uid: string; refresh: string; id: string }): Promise<void> {
	const me = account ?? (await signUp());
	const r = await fetch(`${DOCS}/books/${bookId}/pairing?documentId=${code}`, {
		method: 'POST',
		headers: { Authorization: `Bearer ${me.id}`, 'Content-Type': 'application/json' },
		body: JSON.stringify({ fields: { uid: { stringValue: me.uid }, device: { stringValue: device } } })
	});
	if (!r.ok) throw new ApiError(r.status, 'PAIR_FAILED', 'This link is not valid any more. Make a new one on the computer.');
	saveLink({ book: bookId, uid: me.uid, refresh: me.refresh });
	token = { id: me.id, expires: Date.now() + 50 * 60_000 };
	for (let i = 0; i < 60; i++) {
		await new Promise((res) => setTimeout(res, 3000));
		if (await loadBook(true).catch(() => null)) return;
	}
	saveLink(null);
	throw new ApiError(408, 'PAIR_TIMEOUT', 'The computer did not answer. Keep Mero Lekhapal open on the computer and try again.');
}
