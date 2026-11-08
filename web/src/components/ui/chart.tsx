import { createContext, useContext, type CSSProperties, type ReactElement, type ReactNode } from 'react';
import { ResponsiveContainer } from 'recharts';
import { cn } from '@/lib/utils';

/**
 * Charts in the shadcn/ui style (Recharts). Differences from the upstream component: the series colors are passed as CSS
 * variables through the style property (CSSOM) instead of an inserted <style> element, because the portal's CSP allows
 * <style> only with the per-response nonce; tooltip and legend are small typed components of this file.
 */
export type ChartConfig = Record<string, { label: ReactNode; color: string; unit?: string }>;

const ChartCtx = createContext<ChartConfig>({});

export function useChartConfig() {
  return useContext(ChartCtx);
}

export function ChartContainer({ config, className, children, height = 220, legend }: {
  config: ChartConfig; className?: string; children: ReactElement; height?: number; legend?: string[];
}) {
  const vars: Record<string, string> = {};
  for (const [key, c] of Object.entries(config)) vars[`--color-${key}`] = c.color;
  return (
    <ChartCtx.Provider value={config}>
      <div className={cn('w-full text-xs [&_.recharts-cartesian-axis-tick_text]:fill-muted-foreground [&_.recharts-cartesian-grid_line]:stroke-border/60 [&_.recharts-reference-line_line]:stroke-muted-foreground/60 [&_.recharts-surface]:outline-none', className)}
        style={vars as CSSProperties}>
        <div style={{ height }}>
          <ResponsiveContainer width="100%" height="100%">{children}</ResponsiveContainer>
        </div>
        {legend && <ChartLegend keys={legend} />}
      </div>
    </ChartCtx.Provider>
  );
}

interface TooltipEntry { dataKey?: string | number; value?: number | string | null; color?: string }

/** Tooltip content: the time of the point and one row per series with its unit. */
export function ChartTooltipContent({ active, payload, label, labelFormatter, valueFormatter }: {
  active?: boolean; payload?: TooltipEntry[]; label?: string | number;
  labelFormatter?: (label: string | number) => ReactNode; valueFormatter?: (value: number, key: string) => string;
}) {
  const config = useChartConfig();
  if (!active || !payload?.length) return null;
  return (
    <div className="min-w-40 rounded-lg border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      {label !== undefined && <div className="mb-1 font-medium">{labelFormatter ? labelFormatter(label) : label}</div>}
      <div className="space-y-0.5">
        {payload.map((p) => {
          const key = String(p.dataKey ?? '');
          const c = config[key];
          if (!c) return null;
          const v = typeof p.value === 'number' ? p.value : null;
          return (
            <div key={key} className="flex items-center gap-2">
              <span className="size-2.5 shrink-0 rounded-[2px] bg-(--swatch)" style={{ '--swatch': `var(--color-${key})` } as CSSProperties} />
              <span className="text-muted-foreground">{c.label}</span>
              <span className="ml-auto font-mono tabular-nums">{v === null ? '—' : valueFormatter ? valueFormatter(v, key) : `${v}${c.unit ?? ''}`}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/** Legend of the series of a chart (rendered outside the SVG, so it wraps on small screens). */
export function ChartLegend({ keys }: { keys: string[] }) {
  const config = useChartConfig();
  return (
    <div className="flex flex-wrap items-center justify-center gap-x-4 gap-y-1 pt-2 text-xs">
      {keys.map((k) => [k, config[k]] as const).filter(([, c]) => !!c).map(([k, c]) => (
        <span key={k} className="flex items-center gap-1.5">
          <span className="size-2.5 rounded-[2px] bg-(--swatch)" style={{ '--swatch': `var(--color-${k})` } as CSSProperties} />
          {c?.label}
        </span>
      ))}
    </div>
  );
}
