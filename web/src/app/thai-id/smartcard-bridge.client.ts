/**
 * WebSocket client for ThaiIdBridge — the small Windows agent that exposes PC/SC smart card
 * readers to the browser (browsers cannot talk to CCID readers on Windows by themselves).
 *
 * Framework-agnostic: works in Angular, React, plain TypeScript and Node 22+ (global WebSocket).
 *
 * Protocol (JSON over ws://127.0.0.1:8424/ws):
 *   → {"id":1,"method":"transmit","params":{"handle":"1","apdu":"00A4..."}}
 *   ← {"id":1,"result":{"response":"610A"}}  |  {"id":1,"error":{"code":"SCARD_W_REMOVED_CARD","message":"..."}}
 *   ← {"event":"status","data":{"service":"ready","readers":[...]}}   (pushed on every reader/card change)
 */

export const DEFAULT_BRIDGE_URL = 'ws://127.0.0.1:8424/ws';

export type BridgeConnectionState = 'connecting' | 'connected' | 'disconnected';

export interface ReaderStatus {
  name: string;
  cardPresent: boolean;
  /** A card is inserted but does not answer (usually inserted the wrong way round or a dirty chip). */
  mute: boolean;
  states: string[];
  /** Answer-To-Reset of the inserted card (upper-case hex) or null. */
  atr: string | null;
  eventCount: number;
  /** The bridge's simulated reader (only when started with --simulate). */
  virtual: boolean;
}

export interface BridgeStatus {
  service: 'ready' | 'unavailable';
  error?: string;
  readers: ReaderStatus[];
}

export interface BridgeInfo {
  app: string;
  version: string;
  protocol: number;
  simulator: boolean;
}

export type ShareMode = 'shared' | 'exclusive';
export type CardProtocol = 'T0' | 'T1' | 'any';
export type Disposition = 'leave' | 'reset' | 'unpower' | 'eject';

export interface CardConnection {
  handle: string;
  reader: string;
  protocol: 'T0' | 'T1';
  /** Answer-To-Reset, upper-case hex. */
  atr: string;
}

export class BridgeError extends Error {
  /** Bridge error code, e.g. BRIDGE_NOT_CONNECTED, TIMEOUT or a PC/SC name such as SCARD_W_REMOVED_CARD. */
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = 'BridgeError';
    this.code = code;
  }
}

export interface SmartCardBridgeOptions {
  /** Keep reconnecting when the bridge is not running yet or restarts (default true). */
  autoReconnect?: boolean;
  /** Timeout for a single call in ms (default 20000). */
  callTimeoutMs?: number;
  /** Upper bound for the reconnect back-off in ms (default 5000). */
  maxReconnectDelayMs?: number;
}

interface PendingCall {
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
  timer: ReturnType<typeof setTimeout>;
}

interface BridgeMessage {
  id?: unknown;
  result?: unknown;
  error?: { code?: string; message?: string } | null;
  event?: unknown;
  data?: unknown;
}

type Listener<T> = (value: T) => void;

export class SmartCardBridgeClient {
  readonly url: string;
  private readonly options: Required<SmartCardBridgeOptions>;
  private ws: WebSocket | null = null;
  private running = false;
  private nextId = 1;
  private readonly pending = new Map<number, PendingCall>();
  private readonly statusListeners = new Set<Listener<BridgeStatus>>();
  private readonly stateListeners = new Set<Listener<BridgeConnectionState>>();
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectDelay = 500;
  private currentState: BridgeConnectionState = 'disconnected';
  private currentStatus: BridgeStatus | null = null;
  private currentInfo: BridgeInfo | null = null;

  constructor(url: string = DEFAULT_BRIDGE_URL, options: SmartCardBridgeOptions = {}) {
    this.url = url;
    this.options = { autoReconnect: true, callTimeoutMs: 20_000, maxReconnectDelayMs: 5_000, ...options };
  }

  get state(): BridgeConnectionState {
    return this.currentState;
  }

  /** Last status pushed by the bridge (readers + cards), null while disconnected. */
  get status(): BridgeStatus | null {
    return this.currentStatus;
  }

  /** Bridge name/version, null while disconnected. */
  get info(): BridgeInfo | null {
    return this.currentInfo;
  }

  /** Connects, and keeps reconnecting until stop() — the bridge may be started after the page. */
  start(): void {
    if (this.running) return;
    this.running = true;
    this.open(true);
  }

  stop(): void {
    this.running = false;
    this.clearReconnect();
    const ws = this.ws;
    this.ws = null;
    if (ws) {
      ws.onopen = ws.onclose = ws.onerror = ws.onmessage = null;
      ws.close();
    }
    this.handleDisconnect();
  }

  /** Subscribe to reader/card status. Called immediately with the current status if known. */
  onStatus(listener: Listener<BridgeStatus>): () => void {
    this.statusListeners.add(listener);
    if (this.currentStatus) listener(this.currentStatus);
    return () => this.statusListeners.delete(listener);
  }

  /** Subscribe to the WebSocket connection state. Called immediately with the current state. */
  onConnectionState(listener: Listener<BridgeConnectionState>): () => void {
    this.stateListeners.add(listener);
    listener(this.currentState);
    return () => this.stateListeners.delete(listener);
  }

  /** Resolves once the first status has arrived (connected and ready to use). */
  waitUntilReady(timeoutMs = 3000): Promise<BridgeStatus> {
    if (this.currentStatus) return Promise.resolve(this.currentStatus);
    return new Promise<BridgeStatus>((resolve, reject) => {
      const listener: Listener<BridgeStatus> = (status) => {
        clearTimeout(timer);
        this.statusListeners.delete(listener);
        resolve(status);
      };
      const timer = setTimeout(() => {
        this.statusListeners.delete(listener);
        reject(new BridgeError('BRIDGE_NOT_CONNECTED', notRunningMessage(this.url)));
      }, timeoutMs);
      this.statusListeners.add(listener);
    });
  }

  ping(): Promise<string> {
    return this.call<string>('ping');
  }

  async getStatus(): Promise<BridgeStatus> {
    return normalizeStatus(await this.call<unknown>('getStatus'));
  }

  listReaders(): Promise<string[]> {
    return this.call<string[]>('listReaders');
  }

  async connect(reader: string, options: { shareMode?: ShareMode; protocol?: CardProtocol } = {}): Promise<CardConnection> {
    const result = await this.call<{ handle: string; protocol: 'T0' | 'T1'; atr: string }>('connect', {
      reader,
      shareMode: options.shareMode ?? 'shared',
      protocol: options.protocol ?? 'any',
    });
    return { handle: result.handle, reader, protocol: result.protocol, atr: result.atr };
  }

  /** Sends one APDU; the response includes the two status bytes (SW1 SW2) at the end. */
  async transmit(handle: string, apdu: Uint8Array | readonly number[]): Promise<Uint8Array> {
    const result = await this.call<{ response: string }>('transmit', { handle, apdu: toHex(apdu) });
    return fromHex(result.response);
  }

  async beginTransaction(handle: string): Promise<void> {
    await this.call('beginTransaction', { handle });
  }

  async endTransaction(handle: string, disposition: Disposition = 'leave'): Promise<void> {
    await this.call('endTransaction', { handle, disposition });
  }

  async disconnect(handle: string, disposition: Disposition = 'leave'): Promise<void> {
    await this.call('disconnect', { handle, disposition });
  }

  /** Development only: insert/remove the virtual card (bridge started with --simulate). */
  async simulator(action: 'insert' | 'remove' | 'toggle'): Promise<boolean> {
    const result = await this.call<{ inserted: boolean }>('simulator', { action });
    return result.inserted;
  }

  call<T = unknown>(method: string, params?: Record<string, unknown>): Promise<T> {
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      return Promise.reject(new BridgeError('BRIDGE_NOT_CONNECTED', notRunningMessage(this.url)));
    }
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new BridgeError('TIMEOUT', `Thai ID Bridge did not answer '${method}' in time`));
      }, this.options.callTimeoutMs);
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timer });
      ws.send(JSON.stringify(params === undefined ? { id, method } : { id, method, params }));
    });
  }

  private open(initial: boolean): void {
    this.clearReconnect();
    if (initial) this.setState('connecting');
    let ws: WebSocket;
    try {
      ws = new WebSocket(this.url);
    } catch {
      this.handleDisconnect();
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;
    ws.onopen = () => {
      this.reconnectDelay = 500;
      this.setState('connected');
    };
    ws.onmessage = (event: MessageEvent) => this.handleMessage(event.data);
    ws.onerror = () => {
      /* onclose follows and handles it */
    };
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.handleDisconnect();
      this.scheduleReconnect();
    };
  }

  private handleMessage(raw: unknown): void {
    if (typeof raw !== 'string') return;
    let message: BridgeMessage;
    try {
      message = JSON.parse(raw) as BridgeMessage;
    } catch {
      return;
    }

    if (typeof message.event === 'string') {
      if (message.event === 'hello') {
        this.currentInfo = message.data as BridgeInfo;
      } else if (message.event === 'status') {
        const status = normalizeStatus(message.data);
        this.currentStatus = status;
        for (const listener of [...this.statusListeners]) safeCall(listener, status);
      }
      return;
    }

    if (typeof message.id !== 'number') return;
    const call = this.pending.get(message.id);
    if (!call) return;
    this.pending.delete(message.id);
    clearTimeout(call.timer);
    if (message.error) {
      call.reject(new BridgeError(message.error.code ?? 'BRIDGE_ERROR', message.error.message ?? 'Thai ID Bridge error'));
    } else {
      call.resolve(message.result);
    }
  }

  private handleDisconnect(): void {
    const calls = [...this.pending.values()];
    this.pending.clear();
    for (const call of calls) {
      clearTimeout(call.timer);
      call.reject(new BridgeError('BRIDGE_DISCONNECTED', 'The connection to Thai ID Bridge was lost'));
    }
    this.currentStatus = null;
    this.currentInfo = null;
    this.setState('disconnected');
  }

  private scheduleReconnect(): void {
    if (!this.running || !this.options.autoReconnect) return;
    const delay = this.reconnectDelay;
    this.reconnectDelay = Math.min(this.reconnectDelay * 2, this.options.maxReconnectDelayMs);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.running) this.open(false);
    }, delay);
  }

  private clearReconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private setState(state: BridgeConnectionState): void {
    if (this.currentState === state) return;
    this.currentState = state;
    for (const listener of [...this.stateListeners]) safeCall(listener, state);
  }
}

export function toHex(bytes: Uint8Array | readonly number[]): string {
  let hex = '';
  for (const b of bytes) hex += (b & 0xff).toString(16).padStart(2, '0');
  return hex.toUpperCase();
}

export function fromHex(hex: string): Uint8Array {
  const clean = hex.replace(/[\s:-]/g, '');
  if (clean.length % 2 !== 0 || /[^0-9a-f]/i.test(clean)) throw new Error(`Invalid hex string: ${hex}`);
  const bytes = new Uint8Array(clean.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(clean.substr(i * 2, 2), 16);
  return bytes;
}

function normalizeStatus(data: unknown): BridgeStatus {
  const d = (data ?? {}) as Partial<BridgeStatus>;
  const readers = Array.isArray(d.readers) ? d.readers : [];
  return {
    service: d.service === 'ready' ? 'ready' : 'unavailable',
    error: typeof d.error === 'string' ? d.error : undefined,
    readers: readers.map((r: Partial<ReaderStatus>) => ({
      name: String(r.name ?? ''),
      cardPresent: r.cardPresent === true,
      mute: r.mute === true,
      states: Array.isArray(r.states) ? r.states.map(String) : [],
      atr: typeof r.atr === 'string' ? r.atr : null,
      eventCount: typeof r.eventCount === 'number' ? r.eventCount : 0,
      virtual: r.virtual === true,
    })),
  };
}

function notRunningMessage(url: string): string {
  return `Thai ID Bridge is not running or not reachable at ${url}`;
}

function safeCall<T>(listener: Listener<T>, value: T): void {
  try {
    listener(value);
  } catch (e) {
    console.error('SmartCardBridgeClient listener failed', e);
  }
}
