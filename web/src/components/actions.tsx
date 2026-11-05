import { Ellipsis, type LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { ContextMenu, ContextMenuContent, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator, ContextMenuTrigger } from '@/components/ui/context-menu';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { cn } from '@/lib/utils';

/** One action of a resource: rendered the same way in kebab menus, right-click menus and toolbars. */
export interface Action {
  key: string;
  label: string;
  icon?: LucideIcon;
  onSelect: () => void;
  danger?: boolean;
  disabled?: boolean;
  separatorBefore?: boolean;
  hint?: string;
}

export function DropdownActions({ items, label }: { items: Action[]; label?: string }) {
  return (
    <>
      {label && <DropdownMenuLabel className="max-w-64 truncate">{label}</DropdownMenuLabel>}
      {label && <DropdownMenuSeparator />}
      {items.map((a, i) => (
        <div key={a.key} className="contents">
          {a.separatorBefore && i > 0 && <DropdownMenuSeparator />}
          <DropdownMenuItem variant={a.danger ? 'destructive' : 'default'} disabled={a.disabled} onSelect={() => a.onSelect()} title={a.hint}>
            {a.icon && <a.icon />}{a.label}
          </DropdownMenuItem>
        </div>
      ))}
    </>
  );
}

export function ContextActions({ items, label }: { items: Action[]; label?: string }) {
  return (
    <>
      {label && <ContextMenuLabel className="max-w-64 truncate">{label}</ContextMenuLabel>}
      {label && <ContextMenuSeparator />}
      {items.map((a, i) => (
        <div key={a.key} className="contents">
          {a.separatorBefore && i > 0 && <ContextMenuSeparator />}
          <ContextMenuItem variant={a.danger ? 'destructive' : 'default'} disabled={a.disabled} onSelect={() => a.onSelect()} title={a.hint}>
            {a.icon && <a.icon />}{a.label}
          </ContextMenuItem>
        </div>
      ))}
    </>
  );
}

/** "⋯" button opening the actions of a row. */
export function KebabMenu({ items, label, className }: { items: Action[]; label: string; className?: string }) {
  if (items.length === 0) return null;
  return (
    <DropdownMenu modal={false}>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-sm" className={cn('kebab text-muted-foreground', className)} aria-label={label} title={label}
          onClick={(e) => e.stopPropagation()}>
          <Ellipsis />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-52" onClick={(e) => e.stopPropagation()}>
        <DropdownActions items={items} />
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** Toolbar button with a dropdown of actions (e.g. "Actions ▾" in object headers). */
export function ActionsButton({ items, children, variant = 'outline', label }: { items: Action[]; children: ReactNode; variant?: 'outline' | 'default' | 'ghost' | 'secondary'; label?: string }) {
  if (items.length === 0) return null;
  return (
    <DropdownMenu modal={false}>
      <DropdownMenuTrigger asChild>
        <Button variant={variant} size="sm">{children}</Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-56">
        <DropdownActions items={items} label={label} />
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** Right-click menu for any element (rows, tree nodes, cards). */
export function WithContextMenu({ items, label, children }: { items: Action[]; label?: string; children: ReactNode }) {
  if (items.length === 0) return <>{children}</>;
  return (
    <ContextMenu modal={false}>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="min-w-52">
        <ContextActions items={items} label={label} />
      </ContextMenuContent>
    </ContextMenu>
  );
}

/** Clickable table row: click / Enter opens, right click opens the menu, nested buttons keep their own behavior. */
export function rowLink(open: () => void) {
  return {
    className: 'cursor-pointer',
    tabIndex: 0,
    onClick: (e: React.MouseEvent) => { if (!(e.target as HTMLElement).closest('button, a, input, select, [role=menuitem]')) open(); },
    onKeyDown: (e: React.KeyboardEvent) => { if (e.key === 'Enter' && e.target === e.currentTarget) open(); },
  };
}
