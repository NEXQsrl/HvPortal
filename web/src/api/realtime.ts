import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

type Listener = (payload: { hostId?: string; jobId?: string; status?: string; progress?: number; targetId?: string }) => void;

/**
 * Live updates. The server only pushes identifiers/status; components re-read data via the REST API,
 * which applies authorization. Uses the session cookie (same origin); no tokens in URLs.
 */
class Realtime {
  private connection: HubConnection | null = null;
  private listeners = new Map<string, Set<Listener>>();
  state: 'disconnected' | 'connecting' | 'connected' = 'disconnected';
  private stateListeners = new Set<() => void>();

  async start() {
    if (this.connection) return;
    const c = new HubConnectionBuilder()
      .withUrl('/hubs/events', { withCredentials: true })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.None)
      .build();
    for (const ev of ['inventoryChanged', 'jobChanged']) {
      c.on(ev, (payload) => this.listeners.get(ev)?.forEach((l) => l(payload)));
    }
    c.onreconnecting(() => this.setState('connecting'));
    c.onreconnected(() => {
      this.setState('connected');
      // After a gap we may have missed events: tell everyone to refresh.
      this.listeners.get('inventoryChanged')?.forEach((l) => l({}));
      this.listeners.get('jobChanged')?.forEach((l) => l({}));
    });
    c.onclose(() => this.setState('disconnected'));
    this.connection = c;
    this.setState('connecting');
    try {
      await c.start();
      this.setState(c.state === HubConnectionState.Connected ? 'connected' : 'disconnected');
    } catch {
      this.setState('disconnected');
    }
  }

  async stop() {
    const c = this.connection;
    this.connection = null;
    if (c) await c.stop();
    this.setState('disconnected');
  }

  on(event: 'inventoryChanged' | 'jobChanged', listener: Listener): () => void {
    if (!this.listeners.has(event)) this.listeners.set(event, new Set());
    this.listeners.get(event)!.add(listener);
    return () => this.listeners.get(event)?.delete(listener);
  }

  onState(l: () => void): () => void {
    this.stateListeners.add(l);
    return () => this.stateListeners.delete(l);
  }

  private setState(s: Realtime['state']) {
    this.state = s;
    this.stateListeners.forEach((l) => l());
  }
}

export const realtime = new Realtime();
