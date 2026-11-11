import './lib/csp';
import './index.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Loading } from './components/common';
import { InventoryProvider } from './components/inventory/InventoryProvider';
import { OperationsProvider } from './components/ops/OperationsProvider';
import { PortalUpdateProvider } from './components/admin/PortalUpdateOverlay';
import { IdleGuard } from './components/IdleGuard';
import { ReauthDialog } from './components/ReauthDialog';
import { AppShell } from './components/shell/AppShell';
import { TooltipProvider } from './components/ui/tooltip';
import { I18nProvider } from './i18n';
import { Toaster } from './lib/toast';
import { AccountPage, SettingsPage, UsersPage } from './pages/AdminPages';
import { BackupPage } from './pages/BackupPage';
import { AuditPage } from './pages/AuditPage';
import { TemplatesPage } from './pages/TemplatesPage';
import { ForcedMfaPage, ForcedPasswordChangePage, LoginPage, SetupPage } from './pages/AuthPages';
import { ConsolePage } from './pages/ConsolePage';
import { DashboardPage } from './pages/DashboardPage';
import { GuideConnect, GuideEntra, GuideHome, GuideLayout, GuideRemove, GuideTroubleshooting, GuideUpdate } from './pages/guide/GuidePages';
import { TopicGuidePage } from './pages/guide/TopicGuides';
import { GroupPage, InventoryHome } from './pages/inventory/GroupPage';
import { HostPage } from './pages/inventory/HostPage';
import { ClusterPage } from './pages/inventory/ClusterPage';
import { VmPage } from './pages/inventory/VmPage';
import { JobDetailPage, JobsPage } from './pages/JobsPage';
import { NetworkPage, StoragePage } from './pages/ResourcesPages';
import { SessionProvider, useSession } from './session';
import { applyTheme, readTheme } from './theme';

applyTheme(readTheme());

/** Target of the Entra re-authentication popup: tell the opener and close. */
function ReauthDone() {
  const status = new URLSearchParams(window.location.search).get('status') ?? 'error';
  if (window.opener) {
    window.opener.postMessage({ type: 'hvp-reauth', status }, window.location.origin);
    window.close();
  }
  return <div className="flex h-dvh items-center justify-center p-6 text-center text-sm text-muted-foreground">{status === 'ok' ? 'OK' : status}</div>;
}

/** Inactivity warning and sign-out, for every signed-in user (also on the forced password / MFA pages). */
function SignedInIdleGuard() {
  const { me } = useSession();
  return me ? <IdleGuard key={me.id} /> : null;
}

function Gate() {
  const { state, me, loading } = useSession();
  if (window.location.pathname === '/auth/reauth-done') return <ReauthDone />;
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
            <Route path="/vms" element={<Navigate to="/inventory?view=vms" replace />} />
            <Route path="/vms/:id" element={<VmPage />} />
            <Route path="/hosts" element={<Navigate to="/inventory?view=hosts" replace />} />
            <Route path="/hosts/:id" element={<HostPage />} />
            <Route path="/clusters/:id" element={<ClusterPage />} />
            <Route path="/groups/:id" element={<GroupPage />} />
            <Route path="/networks" element={<NetworkPage />} />
            <Route path="/storage" element={<StoragePage />} />
            <Route path="/jobs" element={<JobsPage />} />
            <Route path="/jobs/:id" element={<JobDetailPage />} />
            <Route path="/users" element={<UsersPage />} />
            <Route path="/audit" element={<AuditPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/account" element={<AccountPage />} />
            <Route path="/guide" element={<GuideLayout />}>
              <Route index element={<GuideHome />} />
              {/* Topic guides for every user: one route, the pages come from TOPIC_GUIDES. */}
              <Route path="topics/:topic" element={<TopicGuidePage />} />
              <Route path="connect" element={<GuideConnect />} />
              <Route path="update" element={<GuideUpdate />} />
              <Route path="remove" element={<GuideRemove />} />
              <Route path="entra" element={<GuideEntra />} />
              <Route path="troubleshooting" element={<GuideTroubleshooting />} />
            </Route>
            <Route path="/backup" element={<BackupPage />} />
            <Route path="/templates" element={<TemplatesPage />} />
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
            {/* Outside the session gate: the update page must stay up while the gateway restarts and the session is lost. */}
            <PortalUpdateProvider>
              <Gate />
              <SignedInIdleGuard />
              <ReauthDialog />
              <Toaster />
            </PortalUpdateProvider>
          </TooltipProvider>
        </BrowserRouter>
      </SessionProvider>
    </I18nProvider>
  </StrictMode>,
);
