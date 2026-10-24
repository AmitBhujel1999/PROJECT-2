/**
 * Classic menu bar: top-level menus open a tree of groups (folders) and
 * pages (leaves). Permissions only hide entries; the API enforces them.
 */

import { adToBs } from '$lib/utilities/nepali-date';
import { toIso } from '$lib/utilities/format';

export interface MenuLeaf {
	label: string;
	href?: string;
	action?: 'logout';
	perm?: string;
}
export interface MenuGroup {
	label: string;
	children: MenuNode[];
	open?: boolean;
}
export type MenuNode = MenuLeaf | MenuGroup | 'separator';

export interface TopMenu {
	label: string;
	/** Alt + key opens the menu; must be a letter of the label. */
	key: string;
	items: MenuNode[];
}

export const isGroup = (n: MenuNode): n is MenuGroup => typeof n === 'object' && 'children' in n;

export const MENUS: TopMenu[] = [
	{
		label: 'Company',
		key: 'c',
		items: [
			{ label: 'Dashboard', href: '/dashboard', perm: 'dashboard.view' },
			{ label: 'Business Settings', href: '/settings', perm: 'settings.view' },
			'separator',
			{ label: 'New Company', href: '/companies/new', perm: 'users.manage' },
			{ label: 'Switch Company', action: 'logout' },
			'separator',
			{ label: 'My Profile', href: '/profile' },
			{ label: 'Change Password', href: '/profile?tab=password' },
			'separator',
			{ label: 'Log Out', action: 'logout' }
		]
	},
	{
		label: 'Administration',
		key: 'a',
		items: [
			{
				label: 'Masters',
				open: true,
				children: [
					{ label: 'Products', href: '/products', perm: 'products.view' },
					{ label: 'Customers', href: '/customers', perm: 'parties.view' },
					{ label: 'Vendors', href: '/vendors', perm: 'parties.view' },
					{ label: 'Expense Categories', href: '/expenses/categories', perm: 'expenses.view' },
					{ label: 'Cash & Bank Accounts', href: '/cash-bank', perm: 'cashbank.view' },
					{ label: 'Chart of Accounts', href: '/journal/ledgers', perm: 'journal.view' },
					{ label: 'Opening Balances', href: '/reports/opening-balances', perm: 'reports.view' }
				]
			},
			{
				label: 'Bulk Update',
				children: [
					{ label: 'Products', href: '/products/bulk', perm: 'products.manage' },
					{ label: 'Customers', href: '/customers/bulk', perm: 'parties.manage' },
					{ label: 'Vendors', href: '/vendors/bulk', perm: 'parties.manage' }
				]
			},
			{
				label: 'Users',
				open: true,
				children: [
					{ label: 'Users', href: '/settings/users', perm: 'users.manage' },
					{ label: 'Roles & Permissions', href: '/settings/roles', perm: 'settings.view' }
				]
			},
			{ label: 'Audit Log', href: '/settings/audit', perm: 'audit.view' },
			{ label: 'Phone Anywhere', href: '/settings/cloud', perm: 'users.manage' }
		]
	},
	{
		label: 'Transactions',
		key: 't',
		items: [
			{
				label: 'Sales',
				open: true,
				children: [
					{ label: 'Add Sale Invoice', href: '/sales/new', perm: 'sales.create' },
					{ label: 'List of Sales', href: '/sales', perm: 'sales.view' }
				]
			},
			{
				label: 'Purchase',
				open: true,
				children: [
					{ label: 'Add Purchase Bill', href: '/purchases/new', perm: 'purchases.create' },
					{ label: 'List of Purchases', href: '/purchases', perm: 'purchases.view' }
				]
			},
			'separator',
			{
				label: 'Customer Receipts',
				children: [
					{ label: 'Add Receipt', href: '/receipts/new', perm: 'receipts.create' },
					{ label: 'List of Receipts', href: '/receipts', perm: 'receipts.view' }
				]
			},
			{
				label: 'Vendor Payments',
				children: [
					{ label: 'Add Payment', href: '/payments/new', perm: 'payments.create' },
					{ label: 'List of Payments', href: '/payments', perm: 'payments.view' }
				]
			},
			{
				label: 'Expenses',
				open: true,
				children: [
					{ label: 'Add Expense', href: '/expenses/new', perm: 'expenses.create' },
					{ label: 'List of Expenses', href: '/expenses', perm: 'expenses.view' }
				]
			},
			{
				label: 'Journal Voucher',
				children: [
					{ label: 'Add Journal Voucher', href: '/journal/new', perm: 'journal.create' },
					{ label: 'List of Journal Vouchers', href: '/journal', perm: 'journal.view' }
				]
			},
			{
				label: 'Contra (Cash ↔ Bank)',
				children: [
					{ label: 'Add Contra', href: '/cash-bank/contra/new', perm: 'contra.create' },
					{ label: 'List of Contra', href: '/cash-bank/contra', perm: 'cashbank.view' }
				]
			},
			'separator',
			{ label: 'Stock Adjustments', href: '/inventory/adjustments', perm: 'inventory.view' }
		]
	},
	{
		label: 'Display',
		key: 'd',
		items: [
			{
				label: 'Inventory',
				open: true,
				children: [
					{ label: 'Stock Register', href: '/inventory', perm: 'inventory.view' },
					{ label: 'Stock Ledger', href: '/inventory/ledger', perm: 'inventory.view' }
				]
			},
			{
				label: 'Cash & Bank',
				open: true,
				children: [
					{ label: 'Cash & Bank Accounts', href: '/cash-bank', perm: 'cashbank.view' },
					{ label: 'Day Book', href: '/reports/day-book', perm: 'reports.view' }
				]
			},
			{
				label: 'Journal',
				children: [
					{ label: 'Chart of Accounts', href: '/journal/ledgers', perm: 'journal.view' },
					{ label: 'List of Journal Vouchers', href: '/journal', perm: 'journal.view' }
				]
			},
			{
				label: 'Accounts Receivable',
				open: true,
				children: [
					{ label: 'Customer Ledger', href: '/receivables/ledger', perm: 'ledgers.view' },
					{ label: 'Customer Statements', href: '/receivables/statements', perm: 'ledgers.view' },
					{ label: 'Receivables Aging', href: '/receivables/aging', perm: 'reports.view' }
				]
			},
			{
				label: 'Accounts Payable',
				open: true,
				children: [
					{ label: 'Vendor Ledger', href: '/payables/ledger', perm: 'ledgers.view' },
					{ label: 'Vendor Statements', href: '/payables/statements', perm: 'ledgers.view' },
					{ label: 'Payables Aging', href: '/payables/aging', perm: 'reports.view' }
				]
			}
		]
	},
	{
		label: 'Reports',
		key: 'r',
		items: [
			{
				label: 'Financial Statements',
				open: true,
				children: [
					{ label: 'Profit & Loss', href: '/reports/profit-loss', perm: 'reports.view' },
					{ label: 'Trial Balance', href: '/reports/trial-balance', perm: 'reports.view' },
					{ label: 'Balance Sheet', href: '/reports/balance-sheet', perm: 'reports.view' },
					{ label: 'Cash Flow', href: '/reports/cash-flow', perm: 'reports.view' },
					{ label: 'Opening Balances', href: '/reports/opening-balances', perm: 'reports.view' }
				]
			},
			'separator',
			{ label: 'Day Book', href: '/reports/day-book', perm: 'reports.view' },
			'separator',
			{ label: 'Sales Report', href: '/reports/sales', perm: 'reports.view' },
			{ label: 'Purchase Report', href: '/reports/purchases', perm: 'reports.view' },
			{ label: 'Expense Report', href: '/reports/expenses', perm: 'reports.view' },
			{ label: 'Stock Report', href: '/reports/stock', perm: 'inventory.view' },
			'separator',
			{ label: 'Receivables Report', href: '/reports/receivables', perm: 'reports.view' },
			{ label: 'Payables Report', href: '/reports/payables', perm: 'reports.view' }
		]
	},
	{
		label: 'Help',
		key: 'h',
		items: [{ label: 'Keyboard Shortcuts', href: '/help' }]
	}
];

/** Drop entries the user cannot open, empty groups and stray separators. */
export function filterMenu(nodes: MenuNode[], can: (perm: string) => boolean): MenuNode[] {
	const out: MenuNode[] = [];
	for (const n of nodes) {
		if (n === 'separator') {
			if (out.length && out[out.length - 1] !== 'separator') out.push(n);
		} else if (isGroup(n)) {
			const children = filterMenu(n.children, can);
			if (children.some((c) => c !== 'separator')) out.push({ ...n, children });
		} else if (!n.perm || can(n.perm)) {
			out.push(n);
		}
	}
	while (out.length && out[out.length - 1] === 'separator') out.pop();
	return out;
}

/** Nepali fiscal year (Shrawan 1 – Asar end) shown in the status line, e.g. "2083-84". */
export function nepaliFiscalYear(d = new Date()): string {
	const bs = adToBs(toIso(d));
	const start = bs.month >= 4 ? bs.year : bs.year - 1;
	return `${start}-${String((start + 1) % 100).padStart(2, '0')}`;
}
