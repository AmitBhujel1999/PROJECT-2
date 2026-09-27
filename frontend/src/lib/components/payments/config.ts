import type { PartyType } from '$lib/types';

export const PAY_SIDES = {
	receipt: {
		type: 'CUSTOMER' as PartyType,
		api: 'customer-receipts',
		route: '/receipts',
		title: 'Customer Receipts',
		label: 'Receipt',
		partyLabel: 'Customer',
		docLabel: 'Invoice',
		docRoute: '/sales',
		createPerm: 'receipts.create',
		cancelPerm: 'receipts.cancel'
	},
	payment: {
		type: 'VENDOR' as PartyType,
		api: 'vendor-payments',
		route: '/payments',
		title: 'Vendor Payments',
		label: 'Payment',
		partyLabel: 'Vendor',
		docLabel: 'Bill',
		docRoute: '/purchases',
		createPerm: 'payments.create',
		cancelPerm: 'payments.cancel'
	}
};
export type PayKind = keyof typeof PAY_SIDES;
