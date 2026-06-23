/**
 * Demo mode loads rich mock data for sales/preview only.
 * Production internal NetWatch must set NEXT_PUBLIC_DEMO_MODE=false.
 */
export const DEMO_MODE = process.env.NEXT_PUBLIC_DEMO_MODE !== 'false';
