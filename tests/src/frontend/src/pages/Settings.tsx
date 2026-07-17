import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge } from '../components/Ui';

export default function Settings() {
  const [settings, setSettings] = useState<any[]>([]);
  const [priorities, setPriorities] = useState<any[]>([]);
  const [edited, setEdited] = useState<Record<string, string>>({});

  const load = () => {
    client.get('/settings').then((r) => setSettings(r.data));
    client.get('/settings/source-priorities').then((r) => setPriorities(r.data));
  };
  useEffect(load, []);

  const save = async (key: string) => {
    await client.put(`/settings/${encodeURIComponent(key)}`, { value: edited[key] });
    setEdited((prev) => { const next = { ...prev }; delete next[key]; return next; });
    load();
  };

  return (
    <>
      <div className="card">
        <h3>System Settings</h3>
        <table className="data">
          <thead><tr><th>Key</th><th>Value</th><th>Description</th><th></th></tr></thead>
          <tbody>
            {settings.map((s) => (
              <tr key={s.key}>
                <td className="muted">{s.key}</td>
                <td>
                  <input
                    value={edited[s.key] ?? s.value}
                    onChange={(e) => setEdited({ ...edited, [s.key]: e.target.value })}
                    style={{ width: 220 }}
                  />
                </td>
                <td className="muted">{s.description}</td>
                <td>
                  {edited[s.key] !== undefined && edited[s.key] !== s.value && (
                    <button onClick={() => save(s.key)}>Save</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h2 className="section-title">Source Priorities</h2>
      <p className="muted" style={{ marginBottom: 10 }}>
        Lower value = more authoritative. Attribute-level entries override the connector's global priority.
      </p>
      <div className="card">
        <table className="data">
          <thead><tr><th>Connector</th><th>Attribute</th><th>Priority</th></tr></thead>
          <tbody>
            {priorities.map((p) => (
              <tr key={p.id}>
                <td><Badge value={p.connector} /></td>
                <td className="muted">{p.attribute ?? '(all attributes)'}</td>
                <td>{p.priority}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
