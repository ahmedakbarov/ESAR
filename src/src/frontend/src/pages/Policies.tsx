import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge } from '../components/Ui';

const ALL_CONTROLS = ['SiemLogSource', 'Edr', 'Antivirus', 'VulnerabilityScanner', 'MonitoringAgent',
  'BackupAgent', 'PatchStatus', 'DiskEncryption', 'AssetClassification'];
const ALL_TYPES = ['WindowsServer', 'LinuxServer', 'VirtualMachine', 'PhysicalServer', 'Workstation',
  'CloudInstance', 'Container', 'KubernetesNode', 'NetworkDevice', 'Firewall', 'LoadBalancer',
  'Switch', 'Router', 'Database', 'Application', 'StorageSystem'];

interface PolicyForm {
  id?: string;
  name: string;
  description: string;
  enabled: boolean;
  priority: number;
  appliesToAssetTypes: string[];
  requiredControls: string[];
  mandatoryControls: string[];
}

const emptyForm: PolicyForm = {
  name: '', description: '', enabled: true, priority: 100,
  appliesToAssetTypes: [], requiredControls: [], mandatoryControls: [],
};

export default function Policies() {
  const [policies, setPolicies] = useState<any[]>([]);
  const [form, setForm] = useState<PolicyForm | null>(null);
  const [error, setError] = useState('');

  const load = () => client.get('/policies').then((r) => setPolicies(r.data));
  useEffect(() => { load(); }, []);

  const toggle = (list: string[], value: string) =>
    list.includes(value) ? list.filter((v) => v !== value) : [...list, value];

  const save = async () => {
    if (!form) return;
    setError('');
    const payload = {
      name: form.name,
      description: form.description,
      enabled: form.enabled,
      priority: form.priority,
      appliesToAssetTypes: form.appliesToAssetTypes,
      appliesToEnvironments: [],
      minCriticality: null,
      requiredControls: form.requiredControls,
      mandatoryControls: form.mandatoryControls,
    };
    try {
      if (form.id) await client.put(`/policies/${form.id}`, payload);
      else await client.post('/policies', payload);
      setForm(null);
      load();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Save failed');
    }
  };

  return (
    <>
      <div className="card">
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
          <h3>Security Baseline Policies</h3>
          <button onClick={() => setForm(form ? null : { ...emptyForm })}>
            {form ? 'Cancel' : 'New policy'}
          </button>
        </div>
        <p className="muted" style={{ marginBottom: 12 }}>
          The first enabled policy (by priority) matching an asset defines which controls it must satisfy.
          Assets not matched by any policy use the full default baseline.
        </p>

        {form && (
          <div className="card" style={{ marginBottom: 16 }}>
            <div className="filters">
              <input placeholder="Policy name" value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })} style={{ width: 240 }} />
              <input placeholder="Description" value={form.description}
                onChange={(e) => setForm({ ...form, description: e.target.value })} style={{ width: 320 }} />
              <input type="number" title="Priority (lower wins)" value={form.priority}
                onChange={(e) => setForm({ ...form, priority: Number(e.target.value) })} style={{ width: 90 }} />
              <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <input type="checkbox" checked={form.enabled}
                  onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled
              </label>
            </div>
            <h3 style={{ marginTop: 10 }}>Applies to asset types (empty = all)</h3>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 10 }}>
              {ALL_TYPES.map((t) => (
                <button key={t} type="button"
                  className={form.appliesToAssetTypes.includes(t) ? '' : 'secondary'}
                  onClick={() => setForm({ ...form, appliesToAssetTypes: toggle(form.appliesToAssetTypes, t) })}>
                  {t}
                </button>
              ))}
            </div>
            <h3>Required controls</h3>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 10 }}>
              {ALL_CONTROLS.map((c) => (
                <button key={c} type="button"
                  className={form.requiredControls.includes(c) ? '' : 'secondary'}
                  onClick={() => setForm({ ...form, requiredControls: toggle(form.requiredControls, c) })}>
                  {c}
                </button>
              ))}
            </div>
            <h3>Mandatory controls (failure ⇒ NonCompliant)</h3>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 10 }}>
              {form.requiredControls.map((c) => (
                <button key={c} type="button"
                  className={form.mandatoryControls.includes(c) ? 'danger' : 'secondary'}
                  onClick={() => setForm({ ...form, mandatoryControls: toggle(form.mandatoryControls, c) })}>
                  {c}
                </button>
              ))}
            </div>
            {error && <div className="error" style={{ marginBottom: 8 }}>{error}</div>}
            <button onClick={save}>Save policy</button>
          </div>
        )}

        <table className="data">
          <thead>
            <tr><th>Priority</th><th>Name</th><th>Applies To</th><th>Required</th>
              <th>Mandatory</th><th>Version</th><th>Enabled</th><th></th></tr>
          </thead>
          <tbody>
            {policies.map((p) => (
              <tr key={p.id}>
                <td>{p.priority}</td>
                <td>{p.name}<div className="muted" style={{ fontSize: 11 }}>{p.description}</div></td>
                <td className="muted">{(p.appliesToAssetTypes ?? []).join(', ') || '(all)'}</td>
                <td className="muted">{(p.requiredControls ?? []).join(', ')}</td>
                <td style={{ color: 'var(--red)' }}>{(p.mandatoryControls ?? []).join(', ')}</td>
                <td className="muted">v{p.version}</td>
                <td>{p.enabled ? <Badge value="Active" /> : <Badge value="Inactive" />}</td>
                <td>
                  <button className="secondary" onClick={() => setForm({
                    id: p.id, name: p.name, description: p.description ?? '', enabled: p.enabled,
                    priority: p.priority, appliesToAssetTypes: p.appliesToAssetTypes ?? [],
                    requiredControls: p.requiredControls ?? [], mandatoryControls: p.mandatoryControls ?? [],
                  })}>Edit</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
