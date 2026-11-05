import { Check, ChevronDown } from 'lucide-react';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput, InputGroupText } from '@/components/ui/input-group';

/** Numeric input with its unit and a drop-down of common values attached on the right. */
export function PresetInput({ id, value, onChange, unit, presets, menuLabel, min, max, step, invalid, className }: {
  id: string;
  value: number;
  onChange: (v: number) => void;
  unit: string;
  presets: { value: number; label: string }[];
  menuLabel: string;
  min?: number;
  max?: number;
  step?: number;
  invalid?: boolean;
  className?: string;
}) {
  return (
    <InputGroup className={className}>
      <InputGroupInput id={id} type="number" inputMode="numeric" min={min} max={max} step={step} value={Number.isFinite(value) ? value : ''}
        aria-invalid={invalid || undefined} onChange={(e) => onChange(Number(e.target.value))} className="tabular-nums" />
      <InputGroupAddon align="inline-end" className="gap-1">
        <InputGroupText>{unit}</InputGroupText>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <InputGroupButton size="icon-xs" variant="ghost" aria-label={menuLabel} className="border-l rounded-none rounded-r-[calc(var(--radius)-3px)]">
              <ChevronDown />
            </InputGroupButton>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="min-w-36">
            <DropdownMenuLabel className="text-xs text-muted-foreground">{menuLabel}</DropdownMenuLabel>
            {presets.map((p) => (
              <DropdownMenuItem key={p.value} onSelect={() => onChange(p.value)} className="justify-between tabular-nums">
                {p.label}{p.value === value && <Check className="size-3.5" />}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </InputGroupAddon>
    </InputGroup>
  );
}
