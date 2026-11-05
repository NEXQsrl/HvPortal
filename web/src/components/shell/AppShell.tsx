import { Layers, Menu, PanelLeftClose, PanelLeftOpen, Search } from 'lucide-react';
import { useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router-dom';
import { realtime } from '@/api/realtime';
import { BrandLogo } from '@/components/common';
import { InventoryTree } from '@/components/inventory/InventoryTree';
import { Button } from '@/components/ui/button';
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from '@/components/ui/resizable';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useI18n, type TKey } from '@/i18n';
import { useLocalStorage, useMediaQuery } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';
import { AccountMenu } from './AccountMenu';
import { CommandPalette } from './CommandPalette';
import { NAV_ITEMS, type NavItem } from './nav';
import { TasksPanel } from './TasksPanel';

function LiveIndicator() {
  const { t } = useI18n();
  const [, force] = useState(0);
  useEffect(() => realtime.onState(() => force((x) => x + 1)), []);
  const s = realtime.state;
  return (
    <span className="badge live hidden items-center gap-1.5 text-xs text-muted-foreground sm:inline-flex" role="status" title={t(('live.' + s) as TKey)}>
      <span className={cn('size-2 rounded-full', s === 'connected' ? 'bg-success' : s === 'connecting' ? 'animate-pulse bg-warning' : 'bg-muted-foreground/50')} />
      <span className="hidden lg:inline">{t(('live.' + s) as TKey)}</span>
    </span>
  );
}

function SidebarNav({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const { t } = useI18n();
  const { pathname } = useLocation();
  const { can } = useSession();
  const visible = (n: NavItem) => !n.permission || n.permission.some(can);
  const sections: { key: NavItem['section']; label?: TKey }[] = [{ key: 'main' }, { key: 'ops', label: 'nav.operations' }, { key: 'admin', label: 'nav.admin' }];
  return (
    <nav className="nav flex flex-col gap-3 px-2 py-3" aria-label="Main">
      {sections.map((s) => {
        const items = NAV_ITEMS.filter((n) => n.section === s.key && visible(n));
        if (items.length === 0) return null;
        return (
          <div key={s.key} className="flex flex-col gap-0.5">
            {s.label && !collapsed && <div className="px-2 pb-1 text-[11px] font-semibold tracking-wide text-muted-foreground/80 uppercase">{t(s.label)}</div>}
            {items.map((n) => {
              const link = (
                <NavLink key={n.to} to={n.to} end={n.end} onClick={onNavigate}
                  className={({ isActive }) => cn('flex h-8 items-center gap-2.5 rounded-md px-2 text-sm text-sidebar-foreground/85 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground',
                    (isActive || n.match?.test(pathname)) && 'bg-sidebar-accent font-medium text-sidebar-accent-foreground', collapsed && 'justify-center px-0', n.planned && 'soon')}>
                  <n.icon className="size-4 shrink-0" />
                  {!collapsed && <span className="truncate">{t(n.label as TKey)}</span>}
                  {!collapsed && n.planned && <span className="ml-auto rounded bg-muted px-1 text-[10px] text-muted-foreground">{t('shell.soon')}</span>}
                </NavLink>
              );
              return collapsed ? (
                <Tooltip key={n.to}><TooltipTrigger asChild>{link}</TooltipTrigger><TooltipContent side="right">{t(n.label as TKey)}</TooltipContent></Tooltip>
              ) : link;
            })}
          </div>
        );
      })}
    </nav>
  );
}

function SidebarFooter({ collapsed }: { collapsed: boolean }) {
  const { t } = useI18n();
  const { state } = useSession();
  if (collapsed) return <div className="sidebar-footer sr-only">{t('app.name')} © NEXQ S.r.l. <a href="https://www.nexq.it">www.nexq.it</a></div>;
  return (
    <footer className="sidebar-footer border-t px-4 py-3 text-[11px] leading-relaxed text-muted-foreground">
      <div className="font-medium text-foreground/80">{t('app.name')} {state?.version ? `v${state.version}` : ''}</div>
      <div>© NEXQ S.r.l.</div>
      <a className="text-primary hover:underline" href="https://www.nexq.it" target="_blank" rel="noopener noreferrer">www.nexq.it</a>
    </footer>
  );
}

const INVENTORY_ROUTES = /^\/(inventory|vms|hosts|groups)(\/|$)/;

/**
 * Application frame: fixed header (search, live status, account), collapsible navigation, inventory tree beside
 * inventory pages (resizable), docked recent-tasks panel. Only inner panels scroll; the document never does.
 */
export function AppShell() {
  const { t } = useI18n();
  const location = useLocation();
  const desktop = useMediaQuery('(min-width: 1024px)');
  const [collapsed, setCollapsed] = useLocalStorage('hvp.sidebar.collapsed', false);
  const [mobileNav, setMobileNav] = useState(false);
  const [mobileTree, setMobileTree] = useState(false);
  const [palette, setPalette] = useState(false);
  const inventoryRoute = INVENTORY_ROUTES.test(location.pathname);

  return (
    <div className="app flex h-dvh flex-col overflow-hidden bg-background">
      <header className="topbar z-20 flex h-12 shrink-0 items-center gap-2 border-b bg-panel px-2 sm:px-3">
        <Button variant="ghost" size="icon-sm" className="lg:hidden" onClick={() => setMobileNav(true)} aria-label={t('shell.menu')}><Menu /></Button>
        <NavLink to="/" className="brand flex items-center pr-2" aria-label={t('app.name')}>
          <span className="sm:hidden"><BrandLogo mark className="h-7" /></span>
          <span className="hidden sm:block"><BrandLogo className="h-6" /></span>
        </NavLink>
        {inventoryRoute && !desktop && (
          <Button variant="outline" size="sm" onClick={() => setMobileTree(true)}><Layers />{t('shell.inventoryPanel')}</Button>
        )}
        <button type="button" onClick={() => setPalette(true)}
          className="mx-auto flex h-8 w-full max-w-md min-w-0 items-center gap-2 rounded-lg border bg-background px-2.5 text-sm text-muted-foreground hover:bg-accent/60">
          <Search className="size-4 shrink-0" />
          <span className="truncate">{t('shell.search')}</span>
          <kbd className="ml-auto hidden rounded border bg-muted px-1.5 font-mono text-[10px] sm:inline">Ctrl K</kbd>
        </button>
        <LiveIndicator />
        <AccountMenu />
      </header>

      <div className="flex min-h-0 flex-1">
        <aside className={cn('sidebar hidden shrink-0 flex-col border-r bg-sidebar lg:flex', collapsed ? 'w-14' : 'w-56')}>
          <div className="min-h-0 flex-1 overflow-auto"><SidebarNav collapsed={collapsed} /></div>
          <SidebarFooter collapsed={collapsed} />
          <div className={cn('flex border-t p-1.5', collapsed ? 'justify-center' : 'justify-end')}>
            <Button variant="ghost" size="icon-sm" onClick={() => setCollapsed(!collapsed)} aria-label={collapsed ? t('shell.expand') : t('shell.collapse')}
              title={collapsed ? t('shell.expand') : t('shell.collapse')}>
              {collapsed ? <PanelLeftOpen /> : <PanelLeftClose />}
            </Button>
          </div>
        </aside>

        <div className="flex min-w-0 flex-1 flex-col">
          <div className="min-h-0 flex-1">
            {inventoryRoute && desktop ? (
              <ResizablePanelGroup orientation="horizontal" className="h-full">
                <ResizablePanel id="tree" defaultSize="22%" minSize="200px" maxSize="45%">
                  <div className="h-full border-r"><InventoryTree /></div>
                </ResizablePanel>
                <ResizableHandle />
                <ResizablePanel id="content" minSize="40%">
                  <main className="content h-full min-w-0 overflow-hidden"><Outlet /></main>
                </ResizablePanel>
              </ResizablePanelGroup>
            ) : (
              <main className="content h-full min-w-0 overflow-hidden"><Outlet /></main>
            )}
          </div>
          <TasksPanel />
        </div>
      </div>

      <Sheet open={mobileNav} onOpenChange={setMobileNav}>
        <SheetContent side="left" className="flex w-72 flex-col gap-0 bg-sidebar p-0">
          <SheetHeader className="border-b"><SheetTitle><BrandLogo className="h-6" /></SheetTitle><SheetDescription className="sr-only">{t('shell.menu')}</SheetDescription></SheetHeader>
          <div className="min-h-0 flex-1 overflow-auto"><SidebarNav collapsed={false} onNavigate={() => setMobileNav(false)} /></div>
          <SidebarFooter collapsed={false} />
        </SheetContent>
      </Sheet>
      <Sheet open={mobileTree} onOpenChange={setMobileTree}>
        <SheetContent side="left" className="flex w-[min(22rem,90vw)] flex-col gap-0 p-0">
          <SheetHeader className="border-b"><SheetTitle>{t('shell.inventoryPanel')}</SheetTitle><SheetDescription className="sr-only">{t('tree.hostsView')}</SheetDescription></SheetHeader>
          <div className="min-h-0 flex-1"><InventoryTree onNavigate={() => setMobileTree(false)} /></div>
        </SheetContent>
      </Sheet>
      <CommandPalette open={palette} onOpenChange={setPalette} />
    </div>
  );
}
