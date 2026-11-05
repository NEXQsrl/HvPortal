import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { ReauthDialog } from './components/ReauthDialog';
import { Loading } from './components/ui';
import { I18nProvider } from './i18n';
import { AccountPage, AuditPage, PlannedPage, SettingsPage, UsersPage } from './pages/AdminPages';
import { ForcedMfaPage, ForcedPasswordChangePage, LoginPage, SetupPage } from './pages/AuthPages';
import { DashboardPage, HostDetailPage, HostsPage, JobDetailPage, JobsPage, VmDetailPage, VmListPage } from './pages/InventoryPages';
import { GuidePage } from './pages/GuidePage';
import { SessionProvider, useSession } from './session';
import './styles.css';

function Gate() {
  const { state, me, loading } = useSession();
  if (loading) return <div className="auth-page"><Loading /></div>;
  if (state?.setupRequired) return <SetupPage />;
  if (!me) return <LoginPage />;
  if (me.restriction === 'password_change') return <ForcedPasswordChangePage />;
  if (me.restriction === 'mfa_enrollment') return <ForcedMfaPage />;
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/vms" element={<VmListPage />} />
        <Route path="/vms/:id" element={<VmDetailPage />} />
        <Route path="/hosts" element={<HostsPage />} />
        <Route path="/hosts/:id" element={<HostDetailPage />} />
        <Route path="/jobs" element={<JobsPage />} />
        <Route path="/jobs/:id" element={<JobDetailPage />} />
        <Route path="/users" element={<UsersPage />} />
        <Route path="/audit" element={<AuditPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="/account" element={<AccountPage />} />
        <Route path="/guide" element={<GuidePage />} />
        <Route path="/networks" element={<PlannedPage titleKey="nav.networks" milestone="Milestone 2" />} />
        <Route path="/storage" element={<PlannedPage titleKey="nav.storage" milestone="Milestone 2-4" />} />
        <Route path="/backup" element={<PlannedPage titleKey="nav.backup" milestone="Milestone 3" />} />
        <Route path="/templates" element={<PlannedPage titleKey="nav.templates" milestone="Milestone 3" />} />
        <Route path="*" element={<PlannedPage titleKey="nav.dashboard" milestone="—" />} />
      </Routes>
    </Layout>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <I18nProvider>
      <SessionProvider>
        <BrowserRouter>
          <Gate />
          <ReauthDialog />
        </BrowserRouter>
      </SessionProvider>
    </I18nProvider>
  </StrictMode>,
);
