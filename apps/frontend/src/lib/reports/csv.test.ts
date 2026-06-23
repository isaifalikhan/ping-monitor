/**
 * @jest-environment node
 */
import { buildReportCsv, reportCsvFilename } from './csv';
import type { ReportSummaryData } from '@/lib/services/reports';

const sampleReport: ReportSummaryData = {
  summary: {
    totalMonitors: 2,
    avgUptime: 99.5,
    avgResponseTime: 120,
    totalIncidents: 3,
    openIncidents: 1,
    resolvedIncidents: 2,
  },
  period: { from: '2026-05-01', to: '2026-05-31' },
  monitors: [
    { name: 'API Gateway', category: 'API', uptime: 99.9, status: 'UP' },
    { name: 'Core Switch', category: 'Network', uptime: 98.5, status: 'DEGRADED' },
  ],
  responseTrend: [{ day: '2026-05-01', ms: 110 }],
  incidentTrend: [{ day: '2026-05-02', count: 2 }],
};

describe('report CSV', () => {
  it('builds multi-section CSV with escaped values', () => {
    const csv = buildReportCsv(sampleReport, 'Q2 Report');
    expect(csv).toContain('Q2 Report');
    expect(csv).toContain('2026-05-01 to 2026-05-31');
    expect(csv).toContain('API Gateway,API,99.9,UP');
    expect(csv).toContain('Daily Response Time');
    expect(csv).toContain('2026-05-01,110');
    expect(csv).toContain('2026-05-02,2');
  });

  it('quotes fields containing commas', () => {
    const data: ReportSummaryData = {
      ...sampleReport,
      monitors: [
        {
          name: 'Site A, Building 2',
          category: 'Network',
          uptime: 100,
          status: 'UP',
        },
      ],
    };
    const csv = buildReportCsv(data);
    expect(csv).toContain('"Site A, Building 2"');
  });

  it('builds deterministic filenames from period', () => {
    expect(reportCsvFilename({ from: '2026-05-01', to: '2026-05-31' })).toBe(
      'netwatch-report-2026-05-01-to-2026-05-31.csv',
    );
  });
});
