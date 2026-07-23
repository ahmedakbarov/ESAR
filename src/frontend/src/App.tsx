import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { AuthProvider, useAuth } from './auth/AuthContext';
import Layout from './components/Layout';
import Login from './pages/Login';
import Dashboard from './pages/Dashboard';
import Assets from './pages/Assets';
import AssetDetail from './pages/AssetDetail';
import Compliance from './pages/Compliance';
import Matching from './pages/Matching';
import Connectors from './pages/Connectors';
import Policies from './pages/Policies';
import Approvals from './pages/Approvals';
import Incidents from './pages/Incidents';
import Reports from './pages/Reports';
import Audit from './pages/Audit';
import Users from './pages/Users';
import Settings, {
  SettingsAuthentication,
  SettingsPriorities,
  SettingsSecurity,
  SettingsSources,
  SettingsSystem,
} from './pages/Settings';

function RequireAuth({ children }: { children: JSX.Element }) {
  const { token } = useAuth();
  const location = useLocation();
  if (!token) return <Navigate to="/login" state={{ from: location }} replace />;
  return children;
}

export default function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route
          path="/"
          element={
            <RequireAuth>
              <Layout />
            </RequireAuth>
          }
        >
          <Route index element={<Dashboard />} />
          <Route path="assets" element={<Assets />} />
          <Route path="assets/:id" element={<AssetDetail />} />
          <Route path="compliance" element={<Compliance />} />
          <Route path="matching" element={<Matching />} />
          <Route path="connectors" element={<Navigate to="/settings/integrations" replace />} />
          <Route path="policies" element={<Policies />} />
          <Route path="approvals" element={<Approvals />} />
          <Route path="incidents" element={<Incidents />} />
          <Route path="reports" element={<Reports />} />
          <Route path="audit" element={<Audit />} />
          <Route path="users" element={<Navigate to="/settings/users-roles" replace />} />
          <Route path="settings" element={<Settings />}>
            <Route index element={<Navigate to="system" replace />} />
            <Route path="security" element={<SettingsSecurity />} />
            <Route path="system" element={<SettingsSystem />} />
            <Route path="users-roles" element={<Users />} />
            <Route path="authentication" element={<SettingsAuthentication />} />
            <Route path="integrations" element={<Connectors />} />
            <Route path="sources" element={<SettingsSources />} />
            <Route path="priorities" element={<SettingsPriorities />} />
            <Route path="audit" element={<Audit />} />
          </Route>
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  );
}
