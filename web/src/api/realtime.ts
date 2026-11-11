import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

export type RealtimeEvent = 'inventoryChanged' | 'jobChanged' | 'groupsChanged' | 'portalUpdating';
type Payload = { hostId?: string; jobId?: string; status?: string; progress?: number; targetId?: string; /** portalUpdating: the version being installed and the one running. */ version?: string; from?: string };
type Listener = (payload: Payload) => void;
/** Events that mean "read your data again": also raised by this page after a reconnection. */
const EVENTS: RealtimeEvent[] = ['inventoryChanged', 'jobChanged', 'groupsChanged'];

/**
 * Live updates. The server only pushes identifiers/status; components re-read data via the REST API,
 * which applies authorization. Uses the session cookie (same origin); no tokens in URLs.
 */
class Realtime {
  private connection: HubConnection | null = null;
  private listeners = new Map<RealtimeEvent, Set<Listener>>();
  state: 'disconnected' | 'connecting' | 'connected' = 'disconnected';
  private stateListeners = new Set<() => void>();

  async start() {
    if (this.connection) return;
    const c = new HubConnectionBuilder()
      .withUrl('/hubs/events', { withCredentials: true })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.None)
      .build();
    for (const ev of EVENTS) {
      c.on(ev, (payload: Payload) => this.emit(ev, payload ?? {}));
    }
    c.on('portalUpdating', (payload: Payload) => this.emit('portalUpdating', payload ?? {}));
    c.onreconnecting(() => this.setState('connecting'));
    c.onreconnected(() => {
      this.setState('connected');
      // After a gap we may have missed events: tell everyone to refresh.
      EVENTS.forEach((e) => this.emit(e, {}));
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

  /** Local notification (e.g. after the UI changed something itself). */
  emit(event: RealtimeEvent, payload: Payload = {}) {
    this.listeners.get(event)?.forEach((l) => l(payload));
  }

  on(event: RealtimeEvent, listener: Listener): () => void {
    if (!this.listeners.has(event)) this.listeners.set(event, new Set());
    this.listeners.get(event)!.add(listener);
    return () => { this.listeners.get(event)?.delete(listener); };
  }

  onState(l: () => void): () => void {
    this.stateListeners.add(l);
    return () => { this.stateListeners.delete(l); };
  }

  private setState(s: Realtime['state']) {
    this.state = s;
    this.stateListeners.forEach((l) => l());
  }
}

export const realtime = new Realtime();
