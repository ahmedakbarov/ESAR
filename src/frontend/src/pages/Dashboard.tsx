import { useEffect, useState } from 'react';
import {
  Bar, BarChart, CartesianGrid, Cell, Line, LineChart, Pie, PieChart,
  ResponsiveContainer, Tooltip, XAxis, YAxis, Legend,
} from 'recharts';
import client, { DashboardSummary } from '../api/client';
import { Panel, StatCard } from '../components/Ui';

const COLORS = ['#3b82f6', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#06b6d4', '#f472b6', '#84cc16'];

interface NameCount { name: string; count: number; }

export default function Dashboard() {
  const [summary, setSummary] = useState<DashboardSummary | null>(null);
  const [byType, setByType] = useState<NameCount[]>([]);
  const [byOs, setByOs] = useState<NameCount[]>([]);
  const [missing, setMissing] = useState<NameCount[]>([]);
  const [growth, setGrowth] = useState<{ date: string; value: number }[]>([]);

  useEffect(() => {
    client.get('/dashboard/summary').then((r) => setSummary(r.data));
    client.get('/dashboard/assets-by-type').then((r) => setByType(r.data));
    client.get('/dashboard/assets-by-os').then((r) => setByOs(r.data));
    client.get('/dashboard/missing-controls').then((r) => setMissing(r.data));
    client.get('/dashboard/asset-growth?days=30').then((r) =>
      setGrowth(r.data.map((p: any) => ({ date: p.date.substring(0, 10), value: p.value }))));
  }, []);

  if (!summary) return <div className="muted">Loading…</div>;

  return (
    <>
      <div className="grid cols-4">
        <StatCard label="Total Assets" value={summary.totalAssets} hint={`${summary.activeAssets} active`} />
        <StatCard label="Critical Assets" value={summary.criticalAssets} tone="warn" />
        <StatCard label="Cloud Assets" value={summary.cloudAssets} />
        <StatCard
          label="Avg Compliance"
          value={`${summary.avgComplianceScore}%`}
          tone={summary.avgComplianceScore >= 80 ? 'good' : 'bad'}
        />
      </div>
      <div className="grid cols-4" style={{ marginTop: 16 }}>
        <StatCard label="Non-Compliant" value={summary.nonCompliantAssets} tone="bad" />
        <StatCard label="Open Incidents" value={summary.openIncidents} tone={summary.openIncidents > 0 ? 'warn' : 'good'} />
        <StatCard label="Pending Match Reviews" value={summary.pendingReviewMatches} />
        <StatCard label="Stale Assets (7d)" value={summary.staleAssets} tone="warn" />
      </div>

      <div className="grid cols-2" style={{ marginTop: 16 }}>
        <Panel title="Asset Growth (30 days)">
          <ResponsiveContainer width="100%" height={240}>
            <LineChart data={growth}>
              <CartesianGrid stroke="#223052" strokeDasharray="3 3" />
              <XAxis dataKey="date" stroke="#8fa3c8" fontSize={11} />
              <YAxis stroke="#8fa3c8" fontSize={11} />
              <Tooltip contentStyle={{ background: '#16213a', border: '1px solid #223052' }} />
              <Line type="monotone" dataKey="value" stroke="#3b82f6" dot={false} strokeWidth={2} />
            </LineChart>
          </ResponsiveContainer>
        </Panel>
        <Panel title="Assets by Type">
          <ResponsiveContainer width="100%" height={240}>
            <PieChart>
              <Pie data={byType} dataKey="count" nameKey="name" outerRadius={90} label>
                {byType.map((_, i) => <Cell key={i} fill={COLORS[i % COLORS.length]} />)}
              </Pie>
              <Tooltip contentStyle={{ background: '#16213a', border: '1px solid #223052' }} />
            </PieChart>
          </ResponsiveContainer>
        </Panel>
      </div>

      <div className="grid cols-2" style={{ marginTop: 16 }}>
        <Panel title="Top Operating Systems">
          <ResponsiveContainer width="100%" height={240}>
            <BarChart data={byOs} layout="vertical">
              <CartesianGrid stroke="#223052" strokeDasharray="3 3" />
              <XAxis type="number" stroke="#8fa3c8" fontSize={11} />
              <YAxis type="category" dataKey="name" width={170} stroke="#8fa3c8" fontSize={11} />
              <Tooltip contentStyle={{ background: '#16213a', border: '1px solid #223052' }} />
              <Bar dataKey="count" fill="#3b82f6" />
            </BarChart>
          </ResponsiveContainer>
        </Panel>
        <Panel title="Missing Security Controls">
          <ResponsiveContainer width="100%" height={240}>
            <BarChart data={missing}>
              <CartesianGrid stroke="#223052" strokeDasharray="3 3" />
              <XAxis dataKey="name" stroke="#8fa3c8" fontSize={10} />
              <YAxis stroke="#8fa3c8" fontSize={11} />
              <Tooltip contentStyle={{ background: '#16213a', border: '1px solid #223052' }} />
              <Bar dataKey="count" fill="#ef4444" />
            </BarChart>
          </ResponsiveContainer>
        </Panel>
      </div>
    </>
  );
}
