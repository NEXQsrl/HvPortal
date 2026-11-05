import { ChevronDown, Languages, LogOut, Monitor, Moon, Sun, UserRound } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem,
  DropdownMenuSeparator, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { useI18n, type Lang } from '@/i18n';
import { useSession } from '@/session';
import { useTheme, type ThemeChoice } from '@/theme';

export function initialsOf(name: string) {
  return name.split(/[\s._@-]+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join('') || '?';
}

export function AccountMenu() {
  const { t, lang, setLang } = useI18n();
  const { me, logout } = useSession();
  const [theme, setTheme] = useTheme();
  const navigate = useNavigate();
  const name = me?.displayName || me?.userName || '';
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" className="account-button h-9 gap-2 px-1.5" aria-label={name}>
          <span className="flex size-7 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground" aria-hidden="true">{initialsOf(name)}</span>
          <span className="account-name hidden max-w-40 truncate text-sm font-medium md:inline">{name}</span>
          <ChevronDown className="size-3.5 text-muted-foreground" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="font-normal">
          <div className="text-sm font-semibold">{name}</div>
          <div className="text-xs text-muted-foreground">{me?.userName}{me?.mfaEnabled ? ' · MFA' : ''}</div>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => navigate('/account')}><UserRound />{t('nav.account')}</DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuGroup>
          <DropdownMenuLabel className="flex items-center gap-2 text-xs text-muted-foreground"><Languages className="size-3.5" />{t('account.language')}</DropdownMenuLabel>
          <DropdownMenuRadioGroup value={lang} onValueChange={(v) => setLang(v as Lang)}>
            <DropdownMenuRadioItem value="it" onSelect={(e) => e.preventDefault()}>Italiano</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="en" onSelect={(e) => e.preventDefault()}>English</DropdownMenuRadioItem>
          </DropdownMenuRadioGroup>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuGroup>
          <DropdownMenuLabel className="flex items-center gap-2 text-xs text-muted-foreground"><Sun className="size-3.5" />{t('account.theme')}</DropdownMenuLabel>
          <DropdownMenuRadioGroup value={theme} onValueChange={(v) => setTheme(v as ThemeChoice)}>
            {([['system', Monitor], ['light', Sun], ['dark', Moon]] as const).map(([v, Icon]) => (
              <DropdownMenuRadioItem key={v} value={v} onSelect={(e) => e.preventDefault()}><Icon />{t(('account.theme.' + v) as never)}</DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem variant="destructive" onSelect={() => void logout()}><LogOut />{t('nav.logout')}</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
