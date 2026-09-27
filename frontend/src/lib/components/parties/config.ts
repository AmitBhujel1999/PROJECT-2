import type { PartyType } from '$lib/types';

export interface SideConfig {
	type: PartyType;
	label: string;
	plural: string;
	api: string; // customers | vendors
	route: string; // /customers | /vendors
	docApi: string;
	docRoute: string;
	docLabel: string;
	docNumberKey: 'invoice_number' | 'bill_number';
	payApi: string;
	payRoute: string;
	payLabel: string;
	payPerm: string;
	agingRoute: string;
	ledgerRoute: string;
	statementRoute: string;
	docTotalLabel: string;
	payTotalLabel: string;
}

export const SIDES: Record<PartyType, SideConfig> = {
	CUSTOMER: {
		type: 'CUSTOMER',
		label: 'Customer',
		plural: 'Customers',
		api: 'customers',
		route: '/customers',
		docApi: 'sales',
		docRoute: '/sales',
		docLabel: 'Invoice',
		docNumberKey: 'invoice_number',
		payApi: 'customer-receipts',
		payRoute: '/receipts',
		payLabel: 'Receipt',
		payPerm: 'receipts.view',
		agingRoute: '/receivables/aging',
		ledgerRoute: '/receivables/ledger',
		statementRoute: '/receivables/statements',
		docTotalLabel: 'Total Sales',
		payTotalLabel: 'Total Received'
	},
	VENDOR: {
		type: 'VENDOR',
		label: 'Vendor',
		plural: 'Vendors',
		api: 'vendors',
		route: '/vendors',
		docApi: 'purchases',
		docRoute: '/purchases',
		docLabel: 'Bill',
		docNumberKey: 'bill_number',
		payApi: 'vendor-payments',
		payRoute: '/payments',
		payLabel: 'Payment',
		payPerm: 'payments.view',
		agingRoute: '/payables/aging',
		ledgerRoute: '/payables/ledger',
		statementRoute: '/payables/statements',
		docTotalLabel: 'Total Purchases',
		payTotalLabel: 'Total Paid'
	}
};
