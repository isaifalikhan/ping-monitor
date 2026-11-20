import { Navigate, Route, Routes } from 'react-router-dom';
import { useAuth } from './auth/AuthContext';
import { Layout } from './components/Layout';
import { LiveProvider } from './live/LiveContext';
import { AccountPage } from './pages/AccountPage';
import { AlertsPage } from './pages/alerts/AlertsPage';
import { DeviceDetailPage } from './pages/devices/DeviceDetailPage';
import { DevicesPage } from './pages/devices/DevicesPage';
import { EventsPage } from './pages/EventsPage';
import { LoginPage } from './pages/LoginPage';
import { OverviewPage } from './pages/OverviewPage';
import { ServersPage } from './pages/ServersPage';
import { SettingsPage } from './pages/settings/SettingsPage';
import { SwitchesPage } from './pages/SwitchesPage';
import { useTheme } from './theme/useTheme';

export function App() {
  const { user, loading, can } = useAuth();
  // Applies the stored theme on every page, including the login page.
  useTheme();

  if (loading) return <div className="empty">Loading…</div>;
  if (!user) return <LoginPage />;
  if (user.mustChangePassword) return <AccountPage forced />;

  return (
    <LiveProvider>
      <Layout>
        <Routes>
          <Route path="/" element={<OverviewPage />} />
          <Route path="/devices" element={<DevicesPage />} />
          <Route path="/devices/:id" element={<DeviceDetailPage />} />
          <Route path="/switches" element={<SwitchesPage />} />
          <Route path="/servers" element={<ServersPage />} />
          <Route path="/events" element={<EventsPage />} />
          <Route path="/alerts" element={<AlertsPage />} />
          <Route path="/account" element={<AccountPage />} />
          {can('Admin') && <Route path="/settings" element={<SettingsPage />} />}
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </Layout>
    </LiveProvider>
  );
}
