import './lib/csp';
import './index.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Loading } from './components/common';
import { InventoryProvider } from './components/inventory/InventoryProvider';
import { OperationsProvider } from './components/ops/OperationsProvider';
import { ReauthDialog } from './components/ReauthDialog';
import { AppShell } from './components/shell/AppShell';
import { TooltipProvider } from './components/ui/tooltip';
import { I18nProvider } from './i18n';
import { Toaster } from './lib/toast';
import { AccountPage, AuditPage, PlannedPage, SettingsPage, UsersPage } from './pages/AdminPages';
import { ForcedMfaPage, ForcedPasswordChangePage, LoginPage, SetupPage } from './pages/AuthPages';
import { ConsolePage } from './pages/ConsolePage';
import { DashboardPage } from './pages/DashboardPage';
import { GuidePage } from './pages/GuidePage';
import { GroupPage, InventoryHome } from './pages/inventory/GroupPage';
import { HostListPage } from './pages/inventory/HostListPage';
import { HostPage } from './pages/inventory/HostPage';
import { VmListPage } from './pages/inventory/VmListPage';
import { VmPage } from './pages/inventory/VmPage';
import { JobDetailPage, JobsPage } from './pages/JobsPage';
import { NetworkPage, StoragePage } from './pages/ResourcesPages';
import { SessionProvider, useSession } from './session';
import { applyTheme, readTheme } from './theme';

applyTheme(readTheme());

function Gate() {
  const { state, me, loading } = useSession();
  if (loading) return <div className="flex h-dvh items-center justify-center"><Loading /></div>;
  if (state?.setupRequired) return <SetupPage />;
  if (!me) return <LoginPage />;
  if (me.restriction === 'password_change') return <ForcedPasswordChangePage />;
  if (me.restriction === 'mfa_enrollment') return <ForcedMfaPage />;
  return (
    <InventoryProvider>
      <OperationsProvider>
        <Routes>
          <Route path="/console/:id" element={<ConsolePage />} />
          <Route element={<AppShell />}>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/inventory" element={<InventoryHome />} />
            <Route path="/vms" element={<VmListPage />} />
            <Route path="/vms/:id" element={<VmPage />} />
            <Route path="/hosts" element={<HostListPage />} />
            <Route path="/hosts/:id" element={<HostPage />} />
            <Route path="/groups/:id" element={<GroupPage />} />
            <Route path="/networks" element={<NetworkPage />} />
            <Route path="/storage" element={<StoragePage />} />
            <Route path="/jobs" element={<JobsPage />} />
            <Route path="/jobs/:id" element={<JobDetailPage />} />
            <Route path="/users" element={<UsersPage />} />
            <Route path="/audit" element={<AuditPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/account" element={<AccountPage />} />
            <Route path="/guide" element={<GuidePage />} />
            <Route path="/backup" element={<PlannedPage titleKey="nav.backup" milestone="Milestone 3" />} />
            <Route path="/templates" element={<PlannedPage titleKey="nav.templates" milestone="Milestone 3" />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </OperationsProvider>
    </InventoryProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <I18nProvider>
      <SessionProvider>
        <BrowserRouter>
          <TooltipProvider delayDuration={400}>
            <Gate />
            <ReauthDialog />
            <Toaster />
          </TooltipProvider>
        </BrowserRouter>
      </SessionProvider>
    </I18nProvider>
  </StrictMode>,
);
