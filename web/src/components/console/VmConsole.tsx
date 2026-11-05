import { Expand, Keyboard, LoaderCircle, Maximize, MonitorOff, MousePointer2, Play, RefreshCw, Scan, SquareArrowOutUpRight, Unplug } from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react';
import { post } from '@/api/client';
import type { Vm } from '@/api/types';
import { errorText, StatusBadge } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useI18n } from '@/i18n';
import { cn } from '@/lib/utils';
import { isKnownKey, scancodes } from './scancodes';

type ConsoleState = 'connecting' | 'running' | 'off' | 'host_offline' | 'unsupported' | 'error' | 'closed' | 'not_found';
interface Status { state: ConsoleState; message?: string | null }

// RGB565 -> 8 bit channel expansion tables.
const EXPAND5 = Uint8Array.from({ length: 32 }, (_, v) => (v << 3) | (v >> 2));
const EXPAND6 = Uint8Array.from({ length: 64 }, (_, v) => (v << 2) | (v >> 4));

async function inflate(data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as BlobPart]).stream().pipeThrough(new DecompressionStream('deflate'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/**
 * Hyper-V console in the browser. The screen is captured by the hypervisor (works before the guest OS boots);
 * keyboard input is sent as scan codes, mouse input through the synthetic mouse when the guest supports it.
 * This is not RDP: no clipboard, audio or devices, and a low frame rate.
 */
export function VmConsole({ vm, onStart, popout = true, className }: { vm: Vm; onStart?: () => void; popout?: boolean; className?: string }) {
  const { t } = useI18n();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const viewRef = useRef<HTMLDivElement>(null);
  const frameRef = useRef<HTMLDivElement>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const chain = useRef<Promise<void>>(Promise.resolve());
  const pressed = useRef(new Set<string>());
  const pendingMove = useRef<{ x: number; y: number } | null>(null);
  const raf = useRef<number | null>(null);
  const [status, setStatus] = useState<Status>({ state: 'connecting' });
  const [guest, setGuest] = useState<{ w: number; h: number } | null>(null);
  const [hasFrame, setHasFrame] = useState(false);
  const [mouse, setMouse] = useState(false);
  const [fit, setFit] = useState(true);
  const [typing, setTyping] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [fullscreen, setFullscreen] = useState(false);

  const send = useCallback((m: object) => {
    const ws = wsRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(m));
  }, []);

  const sendSize = useCallback(() => {
    const el = viewRef.current;
    if (!el) return;
    const dpr = window.devicePixelRatio || 1;
    send(fit ? { t: 'size', w: Math.round(el.clientWidth * dpr), h: Math.round(el.clientHeight * dpr) } : { t: 'size', w: 1920, h: 1200 });
  }, [fit, send]);

  const draw = useCallback(async (buf: ArrayBuffer) => {
    const dv = new DataView(buf);
    if (buf.byteLength < 10 || dv.getUint8(0) !== 1) return;
    const flags = dv.getUint8(1);
    const w = dv.getUint16(2, true);
    const h = dv.getUint16(4, true);
    const gw = dv.getUint16(6, true);
    const gh = dv.getUint16(8, true);
    const raw = await inflate(new Uint8Array(buf, 10));
    const canvas = canvasRef.current;
    if (!canvas || raw.length < w * h * 2) return;
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    const img = ctx.createImageData(w, h);
    const out = new Uint32Array(img.data.buffer);
    for (let i = 0, n = w * h; i < n; i++) {
      const p = raw[2 * i]! | (raw[2 * i + 1]! << 8);
      out[i] = 0xff000000 | (EXPAND5[p & 0x1f]! << 16) | (EXPAND6[(p >> 5) & 0x3f]! << 8) | EXPAND5[(p >> 11) & 0x1f]!;
    }
    ctx.putImageData(img, 0, 0);
    setHasFrame(true);
    setMouse((flags & 1) === 1);
    setGuest((g) => (g && g.w === gw && g.h === gh ? g : { w: gw, h: gh }));
  }, []);

  // ---------------------------------------------------------------- connection
  useEffect(() => {
    let disposed = false;
    setStatus({ state: 'connecting' });
    setHasFrame(false);
    (async () => {
      try {
        const r = await post<{ ticket: string; path: string }>(`/api/v1/vms/${vm.id}/console/tickets`);
        if (disposed) return;
        const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}${r.path}`);
        ws.binaryType = 'arraybuffer';
        wsRef.current = ws;
        ws.onopen = () => { ws.send(JSON.stringify({ t: 'auth', ticket: r.ticket })); sendSize(); };
        ws.onmessage = (ev) => {
          if (typeof ev.data === 'string') {
            try {
              const m = JSON.parse(ev.data) as { t: string; state: ConsoleState; message?: string | null; mouse?: boolean | null };
              if (m.t === 'status') {
                setStatus({ state: m.state, message: m.message });
                if (typeof m.mouse === 'boolean') setMouse(m.mouse);
                if (m.state !== 'running') setHasFrame(false);
              }
            } catch { /* ignore */ }
          } else {
            const buf = ev.data as ArrayBuffer;
            chain.current = chain.current.then(() => draw(buf)).catch(() => undefined);
          }
        };
        ws.onclose = (ev) => {
          if (disposed) return;
          setStatus((s) => (s.state === 'closed' || s.state === 'unsupported' ? s : { state: 'closed', message: ev.reason || s.message || null }));
        };
      } catch (e) {
        if (!disposed) setStatus({ state: 'error', message: errorText(e) });
      }
    })();
    return () => {
      disposed = true;
      wsRef.current?.close();
      wsRef.current = null;
    };
  }, [vm.id, attempt, draw]); // eslint-disable-line react-hooks/exhaustive-deps

  // Viewport size -> requested frame size.
  useEffect(() => {
    const el = viewRef.current;
    if (!el) return;
    let timer: number | undefined;
    const ro = new ResizeObserver(() => { window.clearTimeout(timer); timer = window.setTimeout(sendSize, 250); });
    ro.observe(el);
    sendSize();
    return () => { ro.disconnect(); window.clearTimeout(timer); };
  }, [sendSize]);

  // Hidden tab: pause the frame pump.
  useEffect(() => {
    const on = () => send({ t: document.hidden ? 'pause' : 'resume' });
    document.addEventListener('visibilitychange', on);
    return () => document.removeEventListener('visibilitychange', on);
  }, [send]);

  useEffect(() => {
    const on = () => {
      const fs = document.fullscreenElement === frameRef.current;
      setFullscreen(fs);
      const kb = (navigator as Navigator & { keyboard?: { lock?: () => Promise<void>; unlock?: () => void } }).keyboard;
      if (fs) void kb?.lock?.().catch(() => undefined); else kb?.unlock?.();
      window.setTimeout(sendSize, 300);
    };
    document.addEventListener('fullscreenchange', on);
    return () => document.removeEventListener('fullscreenchange', on);
  }, [sendSize]);

  // ---------------------------------------------------------------- input
  const releaseAll = () => {
    for (const code of pressed.current) {
      const sc = scancodes(code, false);
      if (sc) send({ t: 'k', c: sc });
    }
    pressed.current.clear();
  };

  const onKey = (e: KeyboardEvent<HTMLCanvasElement>, down: boolean) => {
    if (!isKnownKey(e.code)) return;
    e.preventDefault();
    e.stopPropagation();
    const sc = scancodes(e.code, down);
    if (!sc) return;
    if (down) pressed.current.add(e.code); else pressed.current.delete(e.code);
    send({ t: 'k', c: sc });
  };

  const toGuest = (e: MouseEvent<HTMLCanvasElement>) => {
    const c = canvasRef.current;
    if (!c || !guest) return null;
    const r = c.getBoundingClientRect();
    return {
      x: Math.round(Math.min(Math.max(0, (e.clientX - r.left) / r.width), 1) * (guest.w - 1)),
      y: Math.round(Math.min(Math.max(0, (e.clientY - r.top) / r.height), 1) * (guest.h - 1)),
    };
  };

  const onMove = (e: MouseEvent<HTMLCanvasElement>) => {
    if (!mouse) return;
    pendingMove.current = toGuest(e);
    if (raf.current == null) {
      raf.current = requestAnimationFrame(() => {
        raf.current = null;
        if (pendingMove.current) send({ t: 'm', ...pendingMove.current });
      });
    }
  };

  const onButton = (e: MouseEvent<HTMLCanvasElement>, down: boolean) => {
    canvasRef.current?.focus();
    if (!mouse) return;
    e.preventDefault();
    const p = toGuest(e);
    if (p) send({ t: 'm', ...p });
    const b = e.button === 0 ? 1 : e.button === 2 ? 2 : e.button === 1 ? 3 : 0;
    if (b) send({ t: 'b', b, d: down });
  };

  const running = status.state === 'running';
  const overlay = !running || !hasFrame;
  const tone = running ? 'ok' : status.state === 'connecting' ? 'info' : status.state === 'off' ? 'muted' : 'danger';
  const label = {
    connecting: t('con.connecting'), running: t('state.Running'), off: t('con.off'), host_offline: t('con.hostOffline'),
    unsupported: t('con.unsupported'), error: t('con.error'), closed: t('con.closed'), not_found: t('vm.missing'),
  }[status.state];

  const tool = (icon: React.ReactNode, text: string, onClick: () => void, disabled?: boolean) => (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button type="button" variant="ghost" size="sm" onClick={onClick} disabled={disabled} aria-label={text}>{icon}<span className="hidden xl:inline">{text}</span></Button>
      </TooltipTrigger>
      <TooltipContent>{text}</TooltipContent>
    </Tooltip>
  );

  return (
    <div ref={frameRef} className={cn('vm-console flex min-h-0 flex-col bg-card', fullscreen && 'bg-black', className)}>
      <div className="flex flex-wrap items-center gap-1 border-b px-2 py-1">
        <StatusBadge tone={tone}>{status.state === 'connecting' && <LoaderCircle className="animate-spin" />}{label}</StatusBadge>
        {guest && running && <span className="px-1 text-xs text-muted-foreground tabular">{guest.w}×{guest.h}</span>}
        {running && (mouse
          ? <span className="flex items-center gap-1 px-1 text-xs text-muted-foreground"><MousePointer2 className="size-3.5" />OK</span>
          : <span className="flex items-center gap-1 px-1 text-xs text-muted-foreground" title={t('con.noMouse')}><Keyboard className="size-3.5" /></span>)}
        <span className="flex-1" />
        {tool(<Keyboard />, t('con.cad'), () => { send({ t: 'cad' }); canvasRef.current?.focus(); }, !running)}
        {tool(<Keyboard />, t('con.typeText'), () => setTyping(true), !running)}
        {tool(<RefreshCw />, t('con.refresh'), () => send({ t: 'refresh' }), !running)}
        {tool(fit ? <Scan /> : <Expand />, fit ? t('con.actual') : t('con.fit'), () => setFit(!fit))}
        {tool(<Maximize />, t('con.fullscreen'), () => { void frameRef.current?.requestFullscreen?.().then(() => canvasRef.current?.focus()); })}
        {popout && tool(<SquareArrowOutUpRight />, t('con.popout'), () => { window.open(`/console/${vm.id}`, `hvp-console-${vm.id}`, 'popup,width=1280,height=900'); })}
        {(status.state === 'closed' || status.state === 'error' || status.state === 'unsupported') && (
          <Button type="button" size="sm" variant="outline" onClick={() => setAttempt((a) => a + 1)}><Unplug />{t('con.reconnect')}</Button>
        )}
      </div>
      <div ref={viewRef} className={cn('console-view relative min-h-0 flex-1 bg-neutral-950', fit ? 'flex items-center justify-center overflow-hidden' : 'overflow-auto')}>
        <canvas ref={canvasRef} tabIndex={0} aria-label={t('con.title', { name: vm.name })}
          className={cn('console-canvas outline-none focus-visible:ring-2 focus-visible:ring-primary', fit && 'max-h-full max-w-full', overlay && 'opacity-30', mouse && running && 'cursor-crosshair')}
          data-pixelated={!fit}
          onKeyDown={(e) => onKey(e, true)} onKeyUp={(e) => onKey(e, false)} onBlur={releaseAll}
          onMouseMove={onMove} onMouseDown={(e) => onButton(e, true)} onMouseUp={(e) => onButton(e, false)}
          onContextMenu={(e) => e.preventDefault()} width={1024} height={768} />
        {overlay && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 p-6 text-center text-neutral-200">
            {status.state === 'connecting' || (running && !hasFrame) ? <LoaderCircle className="size-8 animate-spin text-neutral-400" />
              : <MonitorOff className="size-10 text-neutral-500" />}
            <div className="text-sm font-medium">{running ? t('con.connecting') : label}</div>
            {status.state === 'off' && <div className="max-w-md text-xs text-neutral-400">{t('con.offHelp')}</div>}
            {status.message && status.state !== 'off' && <div className="max-w-md text-xs break-words text-neutral-400">{status.message}</div>}
            {status.state === 'off' && onStart && vm.allowedOperations.includes('Start') && (
              <Button type="button" size="sm" onClick={onStart}><Play />{t('op.Start')}</Button>
            )}
          </div>
        )}
      </div>
      {!fullscreen && (
        <div className="flex flex-wrap gap-x-4 gap-y-0.5 border-t px-3 py-1.5 text-[11px] text-muted-foreground">
          <span>{t('con.keyboardHint')}</span>
          {running && !mouse && <span>{t('con.noMouse')}</span>}
          <span>{t('con.limits')}</span>
          <span>{t('con.idle')}</span>
        </div>
      )}
      {typing && <TypeTextDialog onSend={(s) => { send({ t: 'type', s }); canvasRef.current?.focus(); }} onClose={() => setTyping(false)} />}
    </div>
  );
}

function TypeTextDialog({ onSend, onClose }: { onSend: (s: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [text, setText] = useState('');
  const valid = text.length > 0 && text.length <= 512 && /^[\x20-\x7e\n\t]*$/.test(text);
  return (
    <ActionDialog title={t('con.typeTitle')} confirmLabel={t('con.send')} disabled={!valid} onClose={onClose} onConfirm={async () => onSend(text)}>
      <Field label={t('con.typeTitle')} htmlFor="con-text" hint={t('con.typeHelp')} aside={`${text.length}/512`}>
        <Textarea id="con-text" rows={4} value={text} onChange={(e) => setText(e.target.value)} autoFocus spellCheck={false} autoComplete="off" className="font-mono" />
      </Field>
    </ActionDialog>
  );
}
