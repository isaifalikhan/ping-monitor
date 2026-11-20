import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import { ErrorBox, Modal } from '../../components/Modal';

interface ImportResult {
  created: number;
  skipped: number;
  errors: { line: number; message: string }[];
}

export function ImportDialog({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const upload = async () => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const form = new FormData();
      form.append('file', file);
      setResult(await api.upload<ImportResult>('/api/devices/import', form));
      await qc.invalidateQueries({ queryKey: ['devices'] });
      await qc.invalidateQueries({ queryKey: ['groups'] });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title="Import devices from CSV"
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>{result ? 'Close' : 'Cancel'}</button>
          {!result && (
            <button className="primary" disabled={!file || busy} onClick={() => void upload()}>
              Import
            </button>
          )}
        </>
      }
    >
      <div className="muted">
        Columns: <span className="mono">name, ip, type, location, group, tags, snmp_credential, poll_interval, parent, notes</span>. Only{' '}
        <strong>name</strong> and <strong>ip</strong> are required. Groups are given as a path (e.g. <span className="mono">HQ/Building A/Floor 1</span>) and are
        created if missing. Existing IP addresses are skipped.
      </div>
      <div>
        <a href="/api/devices/import/template" download>
          Download template
        </a>
      </div>
      <input type="file" accept=".csv,text/csv" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
      <ErrorBox error={error} />
      {result && (
        <>
          <div className="ok-box">
            {result.created} device(s) created, {result.skipped} skipped.
          </div>
          {result.errors.length > 0 && (
            <div className="table-wrap" style={{ maxHeight: 240 }}>
              <table className="data">
                <thead>
                  <tr>
                    <th>Line</th>
                    <th>Message</th>
                  </tr>
                </thead>
                <tbody>
                  {result.errors.map((e, i) => (
                    <tr key={i}>
                      <td className="num">{e.line}</td>
                      <td>{e.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </Modal>
  );
}
