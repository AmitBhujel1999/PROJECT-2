import type { StatementRow } from '$lib/types';

/** Where a statement line drills down to. */
export function rowHref(row: StatementRow, kind: 'ledger' | 'category' | 'cash' = 'ledger'): string | null {
	switch (row.link) {
		case 'receivables':
			return '/reports/receivables';
		case 'payables':
			return '/reports/payables';
		case 'stock':
			return '/reports/stock';
		case 'cash-bank':
			return row.id ? `/cash-bank/${row.id}` : '/cash-bank';
		case 'profit-loss':
			return '/reports/profit-loss';
		case 'receipts':
			return '/receipts';
		case 'payments':
			return '/payments';
		case 'expenses':
			return '/reports/expenses';
	}
	if (!row.id) return null;
	if (kind === 'category') return `/reports/expenses?category=${row.id}`;
	if (kind === 'cash') return `/cash-bank/${row.id}`;
	return `/journal/ledgers/${row.id}`;
}
