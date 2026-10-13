/**
 * Which calendar dates are shown and typed in: 'AD' (English) or 'BS' (Nepali
 * Bikram Sambat). Set per company in Business Settings; the API always works
 * in A.D. ISO dates, only the display and date fields change.
 */
export type DateSystem = 'AD' | 'BS';

export const calendar = $state<{ system: DateSystem }>({ system: 'AD' });
