import { Injectable, InjectionToken, OnDestroy, computed, inject, signal } from '@angular/core';
import {
  BridgeConnectionState,
  BridgeError,
  BridgeInfo,
  BridgeStatus,
  DEFAULT_BRIDGE_URL,
  ReaderStatus,
  SmartCardBridgeClient,
} from './smartcard-bridge.client';
import { ReadProgress, ThaiIdCardData, ThaiIdCardError, readThaiIdCard } from './thai-id-card';

/** Override in app.config.ts if the bridge runs on another port: { provide: THAI_ID_BRIDGE_URL, useValue: 'ws://127.0.0.1:9000/ws' } */
export const THAI_ID_BRIDGE_URL = new InjectionToken<string>('THAI_ID_BRIDGE_URL', {
  providedIn: 'root',
  factory: () => DEFAULT_BRIDGE_URL,
});

export const THAI_ID_MESSAGES = {
  bridgeDown: 'ไม่พบโปรแกรม Thai ID Bridge บนเครื่องนี้ — กรุณาเปิด ThaiIdBridge.exe แล้วลองใหม่',
  noReader: 'ไม่พบเครื่องอ่านบัตร — กรุณาเสียบเครื่องอ่านบัตร',
  noCard: 'ไม่พบบัตรในเครื่องอ่าน หรือบัตรถูกถอดออกระหว่างอ่าน — กรุณาเสียบบัตรค้างไว้จนอ่านเสร็จ',
  mute: 'บัตรไม่ตอบสนอง — ลองถอดแล้วเสียบใหม่ โดยหันด้านชิปให้ถูกต้อง',
  sharing: 'เครื่องอ่านบัตรถูกโปรแกรมอื่นใช้งานอยู่ — ปิดโปรแกรมอ่านบัตรอื่นแล้วลองใหม่',
  notThaiId: 'บัตรนี้ไม่ใช่บัตรประจำตัวประชาชนไทย',
  timeout: 'หมดเวลาการอ่านบัตร — ลองใหม่อีกครั้ง',
} as const;

const RETRYABLE = new Set(['SCARD_W_RESET_CARD', 'SCARD_E_SHARING_VIOLATION', 'SCARD_E_NOT_TRANSACTED', 'SCARD_E_COMM_DATA_LOST']);

/**
 * Angular facade: connection state, readers, auto-read on card insert, and the last card read — all as signals.
 *
 *   readonly id = inject(ThaiIdCardService);
 *   effect(() => { const card = this.id.card(); if (card) this.api.saveCustomer(toPayload(card)); });
 */
@Injectable({ providedIn: 'root' })
export class ThaiIdCardService implements OnDestroy {
  private readonly client = new SmartCardBridgeClient(inject(THAI_ID_BRIDGE_URL));
  private readonly unsubscribe: Array<() => void> = [];
  private previousReaders = new Map<string, ReaderStatus>();
  private autoReadTimer: ReturnType<typeof setTimeout> | null = null;

  readonly connection = signal<BridgeConnectionState>('connecting');
  readonly bridgeInfo = signal<BridgeInfo | null>(null);
  readonly bridgeStatus = signal<BridgeStatus | null>(null);
  readonly readers = computed(() => this.bridgeStatus()?.readers ?? []);
  readonly selectedReaderName = signal<string | null>(null);
  readonly selectedReader = computed(() => this.readers().find((r) => r.name === this.selectedReaderName()) ?? null);
  readonly card = signal<ThaiIdCardData | null>(null);
  readonly reading = signal(false);
  readonly progress = signal<ReadProgress | null>(null);
  readonly error = signal<string | null>(null);
  /** Read automatically when a card is inserted into the selected reader. */
  readonly autoRead = signal(true);
  /** Forget the card data when the card is pulled out. */
  readonly clearOnRemove = signal(false);
  readonly url = this.client.url;

  constructor() {
    this.unsubscribe.push(
      this.client.onConnectionState((state) => {
        this.connection.set(state);
        if (state !== 'connected') {
          this.bridgeStatus.set(null);
          this.bridgeInfo.set(null);
          this.previousReaders.clear();
        }
      }),
      this.client.onStatus((status) => this.handleStatus(status)),
    );
    this.client.start();
  }

  /** Low-level access (raw APDUs) for other card types. */
  get bridge(): SmartCardBridgeClient {
    return this.client;
  }

  selectReader(name: string): void {
    this.selectedReaderName.set(name);
    const reader = this.readers().find((r) => r.name === name);
    if (reader?.cardPresent && !reader.mute && this.autoRead() && this.card()?.reader !== name) this.scheduleAutoRead(name, 0);
  }

  /**
   * Reads the card in the given (default: selected) reader. Resolves with the data, or null on failure —
   * the reason is in error().
   */
  async read(readerName: string | null = this.selectedReaderName(), options: { includePhoto?: boolean } = {}): Promise<ThaiIdCardData | null> {
    if (this.reading()) return null;
    if (this.connection() !== 'connected') {
      this.error.set(THAI_ID_MESSAGES.bridgeDown);
      return null;
    }
    if (!readerName) {
      this.error.set(THAI_ID_MESSAGES.noReader);
      return null;
    }
    this.reading.set(true);
    this.error.set(null);
    this.progress.set({ step: 'connect', done: 0, total: 1, percent: 0 });
    try {
      const data = await this.readWithRetry(readerName, options.includePhoto ?? true);
      this.card.set(data);
      return data;
    } catch (e) {
      this.error.set(describeError(e));
      return null;
    } finally {
      this.reading.set(false);
      this.progress.set(null);
    }
  }

  clear(): void {
    this.card.set(null);
    this.error.set(null);
  }

  /** Development only (bridge started with --simulate). */
  async simulate(action: 'insert' | 'remove'): Promise<void> {
    try {
      await this.client.simulator(action);
    } catch (e) {
      this.error.set(describeError(e));
    }
  }

  ngOnDestroy(): void {
    if (this.autoReadTimer) clearTimeout(this.autoReadTimer);
    this.unsubscribe.forEach((off) => off());
    this.client.stop();
  }

  private async readWithRetry(readerName: string, includePhoto: boolean): Promise<ThaiIdCardData> {
    const onProgress = (p: ReadProgress) => this.progress.set(p);
    try {
      return await readThaiIdCard(this.client, readerName, { includePhoto, onProgress });
    } catch (e) {
      // Right after insertion Windows may still be probing the card — one quiet retry.
      if (!(e instanceof BridgeError) || !RETRYABLE.has(e.code)) throw e;
      await new Promise((resolve) => setTimeout(resolve, 400));
      return await readThaiIdCard(this.client, readerName, { includePhoto, onProgress });
    }
  }

  private handleStatus(status: BridgeStatus): void {
    this.bridgeStatus.set(status);
    this.bridgeInfo.set(this.client.info);
    const previous = this.previousReaders;
    this.previousReaders = new Map(status.readers.map((r) => [r.name, r] as const));

    // Keep a sensible reader selected: one with a card, else a real reader, else anything.
    const selected = this.selectedReaderName();
    if (!selected || !this.previousReaders.has(selected)) {
      const pick =
        status.readers.find((r) => r.cardPresent && !r.mute) ?? status.readers.find((r) => !r.virtual) ?? status.readers[0];
      this.selectedReaderName.set(pick?.name ?? null);
    }

    for (const reader of status.readers) {
      const before = previous.get(reader.name);
      const wasPresent = before?.cardPresent === true;
      if (wasPresent && !reader.cardPresent) {
        if (this.clearOnRemove() && this.card()?.reader === reader.name) this.card.set(null);
        if (reader.name === this.selectedReaderName() && !this.reading()) this.error.set(null);
      }
      const inserted = reader.cardPresent && (!wasPresent || before?.atr !== reader.atr);
      if (!inserted) continue;
      // Follow the card: a card inserted into another (already known) reader makes that reader the selected one.
      if (before && reader.name !== this.selectedReaderName() && !this.reading()) this.selectedReaderName.set(reader.name);
      if (reader.name !== this.selectedReaderName()) continue;
      if (reader.mute) this.error.set(THAI_ID_MESSAGES.mute);
      else if (this.autoRead()) this.scheduleAutoRead(reader.name, 300);
    }
  }

  private scheduleAutoRead(readerName: string, delayMs: number): void {
    if (this.autoReadTimer) clearTimeout(this.autoReadTimer);
    this.autoReadTimer = setTimeout(() => {
      this.autoReadTimer = null;
      const reader = this.readers().find((r) => r.name === readerName);
      if (reader?.cardPresent && !reader.mute) void this.read(readerName);
    }, delayMs);
  }
}

export function describeError(e: unknown): string {
  const code = e instanceof BridgeError || e instanceof ThaiIdCardError ? e.code : '';
  switch (code) {
    case 'BRIDGE_NOT_CONNECTED':
    case 'BRIDGE_DISCONNECTED':
      return THAI_ID_MESSAGES.bridgeDown;
    case 'SCARD_E_NO_SMARTCARD':
    case 'SCARD_W_REMOVED_CARD':
      return THAI_ID_MESSAGES.noCard;
    case 'SCARD_W_UNRESPONSIVE_CARD':
    case 'SCARD_W_UNPOWERED_CARD':
    case 'SCARD_W_UNSUPPORTED_CARD':
    case 'SCARD_E_COMM_DATA_LOST':
    case 'SCARD_F_COMM_ERROR':
      return THAI_ID_MESSAGES.mute;
    case 'SCARD_E_SHARING_VIOLATION':
      return THAI_ID_MESSAGES.sharing;
    case 'SCARD_E_UNKNOWN_READER':
    case 'SCARD_E_READER_UNAVAILABLE':
    case 'SCARD_E_NO_READERS_AVAILABLE':
    case 'SCARD_E_NO_SERVICE':
    case 'SCARD_E_SERVICE_STOPPED':
      return THAI_ID_MESSAGES.noReader;
    case 'NOT_THAI_ID_CARD':
      return THAI_ID_MESSAGES.notThaiId;
    case 'TIMEOUT':
      return THAI_ID_MESSAGES.timeout;
  }
  const message = e instanceof Error ? e.message : String(e);
  return `อ่านบัตรไม่สำเร็จ${code ? ` (${code})` : ''}: ${message}`;
}
