import {
	LayoutDashboard,
	ShoppingCart,
	Truck,
	HandCoins,
	Wallet,
	Package,
	Boxes,
	ScrollText,
	SlidersHorizontal,
	Users,
	Building2,
	BookOpen,
	Clock,
	FileText,
	ChartColumn,
	UserCog,
	ShieldCheck,
	History,
	Settings
} from '@lucide/svelte';
import type { Component } from 'svelte';

export interface NavItem {
	label: string;
	href: string;
	icon: Component<{ class?: string }>;
	perm?: string;
}
export interface NavGroup {
	label: string;
	items: NavItem[];
}

export const NAV: NavGroup[] = [
	{ label: '', items: [{ label: 'Dashboard', href: '/dashboard', icon: LayoutDashboard, perm: 'dashboard.view' }] },
	{
		label: 'Transactions',
		items: [
			{ label: 'Sales', href: '/sales', icon: ShoppingCart, perm: 'sales.view' },
			{ label: 'Purchases', href: '/purchases', icon: Truck, perm: 'purchases.view' },
			{ label: 'Customer Receipts', href: '/receipts', icon: HandCoins, perm: 'receipts.view' },
			{ label: 'Vendor Payments', href: '/payments', icon: Wallet, perm: 'payments.view' }
		]
	},
	{
		label: 'Inventory',
		items: [
			{ label: 'Products', href: '/products', icon: Package, perm: 'products.view' },
			{ label: 'Stock Register', href: '/inventory', icon: Boxes, perm: 'inventory.view' },
			{ label: 'Stock Ledger', href: '/inventory/ledger', icon: ScrollText, perm: 'inventory.view' },
			{ label: 'Stock Adjustments', href: '/inventory/adjustments', icon: SlidersHorizontal, perm: 'inventory.view' }
		]
	},
	{
		label: 'Parties',
		items: [
			{ label: 'Customers', href: '/customers', icon: Users, perm: 'parties.view' },
			{ label: 'Vendors', href: '/vendors', icon: Building2, perm: 'parties.view' }
		]
	},
	{
		label: 'Accounts Receivable',
		items: [
			{ label: 'Customer Ledger', href: '/receivables/ledger', icon: BookOpen, perm: 'ledgers.view' },
			{ label: 'Receivables Aging', href: '/receivables/aging', icon: Clock, perm: 'reports.view' },
			{ label: 'Customer Statements', href: '/receivables/statements', icon: FileText, perm: 'ledgers.view' }
		]
	},
	{
		label: 'Accounts Payable',
		items: [
			{ label: 'Vendor Ledger', href: '/payables/ledger', icon: BookOpen, perm: 'ledgers.view' },
			{ label: 'Payables Aging', href: '/payables/aging', icon: Clock, perm: 'reports.view' },
			{ label: 'Vendor Statements', href: '/payables/statements', icon: FileText, perm: 'ledgers.view' }
		]
	},
	{
		label: 'Reports',
		items: [
			{ label: 'Sales Report', href: '/reports/sales', icon: ChartColumn, perm: 'reports.view' },
			{ label: 'Purchase Report', href: '/reports/purchases', icon: ChartColumn, perm: 'reports.view' },
			{ label: 'Stock Report', href: '/reports/stock', icon: ChartColumn, perm: 'inventory.view' },
			{ label: 'Receivables Report', href: '/reports/receivables', icon: ChartColumn, perm: 'reports.view' },
			{ label: 'Payables Report', href: '/reports/payables', icon: ChartColumn, perm: 'reports.view' }
		]
	},
	{
		label: 'Administration',
		items: [
			{ label: 'Users', href: '/settings/users', icon: UserCog, perm: 'users.manage' },
			{ label: 'Roles', href: '/settings/roles', icon: ShieldCheck, perm: 'settings.view' },
			{ label: 'Audit Log', href: '/settings/audit', icon: History, perm: 'audit.view' },
			{ label: 'Settings', href: '/settings', icon: Settings, perm: 'settings.view' }
		]
	}
];
