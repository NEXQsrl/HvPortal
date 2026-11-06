import { Menu, PanelLeftClose, PanelLeftOpen, Search } from 'lucide-react';
import { useEffect, useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { realtime } from '@/api/realtime';
import { BrandLogo } from '@/components/common';
import { Button } from '@/components/ui/button';
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from '@/components/ui/resizable';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { useI18n, type TKey } from '@/i18n';
import { useLocalStorage, useMediaQuery } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { AccountMenu } from './AccountMenu';
import { CommandPalette } from './CommandPalette';
import { Navigator } from './Navigator';
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

/**
 * Application frame: fixed header (search, live status, account) and, on every page, the navigator on the left
 * (main navigation as icons + the tree of the current page, resizable and hideable) beside the page; docked
 * recent-tasks panel below. Only inner panels scroll; the document never does.
 */
export function AppShell() {
  const { t } = useI18n();
  const desktop = useMediaQuery('(min-width: 1024px)');
  const [hidden, setHidden] = useLocalStorage('hvp.navigator.hidden', false);
  const [mobileNav, setMobileNav] = useState(false);
  const [palette, setPalette] = useState(false);
  const toggleLabel = hidden ? t('shell.expand') : t('shell.collapse');

  return (
    <div className="app flex h-dvh flex-col overflow-hidden bg-background">
      <header className="topbar z-20 flex h-12 shrink-0 items-center gap-2 border-b bg-panel px-2 sm:px-3">
        <Button variant="ghost" size="icon-sm" className="lg:hidden" onClick={() => setMobileNav(true)} aria-label={t('shell.menu')}><Menu /></Button>
        <Button variant="ghost" size="icon-sm" className="hidden lg:inline-flex" onClick={() => setHidden(!hidden)} aria-label={toggleLabel} title={toggleLabel}>
          {hidden ? <PanelLeftOpen /> : <PanelLeftClose />}
        </Button>
        <NavLink to="/" className="brand flex items-center pr-2" aria-label={t('app.name')}>
          <span className="sm:hidden"><BrandLogo mark className="h-7" /></span>
          <span className="hidden sm:block"><BrandLogo className="h-6" /></span>
        </NavLink>
        <button type="button" onClick={() => setPalette(true)}
          className="mx-auto flex h-8 w-full max-w-md min-w-0 items-center gap-2 rounded-lg border bg-background px-2.5 text-sm text-muted-foreground hover:bg-accent/60">
          <Search className="size-4 shrink-0" />
          <span className="truncate">{t('shell.search')}</span>
          <kbd className="ml-auto hidden rounded border bg-muted px-1.5 font-mono text-[10px] sm:inline">Ctrl K</kbd>
        </button>
        <LiveIndicator />
        <AccountMenu />
      </header>

      <div className="flex min-h-0 min-w-0 flex-1 flex-col">
        <div className="min-h-0 flex-1">
          {desktop && !hidden ? (
            <ResizablePanelGroup orientation="horizontal" className="h-full">
              <ResizablePanel id="tree" defaultSize="356px" minSize="200px" maxSize="45%">
                <aside className="sidebar h-full border-r"><Navigator /></aside>
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

      <Sheet open={mobileNav} onOpenChange={setMobileNav}>
        <SheetContent side="left" className="flex w-[min(22rem,90vw)] flex-col gap-0 p-0">
          <SheetHeader className="border-b"><SheetTitle><BrandLogo className="h-6" /></SheetTitle><SheetDescription className="sr-only">{t('shell.menu')}</SheetDescription></SheetHeader>
          <div className="min-h-0 flex-1"><Navigator onNavigate={() => setMobileNav(false)} /></div>
        </SheetContent>
      </Sheet>
      <CommandPalette open={palette} onOpenChange={setPalette} />
    </div>
  );
}
