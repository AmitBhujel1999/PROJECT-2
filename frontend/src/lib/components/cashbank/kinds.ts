import type { AccountKind } from '$lib/types';

export const ACCOUNT_KINDS: { value: AccountKind; label: string }[] = [
	{ value: 'CASH', label: 'Cash' },
	{ value: 'BANK', label: 'Bank' },
	{ value: 'WALLET', label: 'Digital wallet (eSewa, Khalti…)' },
	{ value: 'OTHER', label: 'Other' }
];
