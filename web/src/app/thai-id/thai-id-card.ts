/**
 * Thai national ID card ("dip chip") reader — APDU protocol + parsing, in plain TypeScript.
 * Works through any transport that can send APDUs; here: SmartCardBridgeClient (ThaiIdBridge on Windows).
 *
 * Card layout (MOI applet A0 00 00 00 54 48 00 01, read with 80 B0 <offset> 02 00 <length>):
 * text is TIS-620 (windows-874), dates are YYYYMMDD in the Buddhist era, fields inside a value are split by '#'.
 */
import { CardConnection, SmartCardBridgeClient, fromHex, toHex } from './smartcard-bridge.client';

export const THAI_ID_APPLET_AID = [0xa0, 0x00, 0x00, 0x00, 0x54, 0x48, 0x00, 0x01] as const;

export const THAI_ID_FIELDS = {
  citizenId: { offset: 0x0004, length: 13 },
  nameTh: { offset: 0x0011, length: 100 },
  nameEn: { offset: 0x0075, length: 100 },
  birthDate: { offset: 0x00d9, length: 8 },
  gender: { offset: 0x00e1, length: 1 },
  issuer: { offset: 0x00f6, length: 100 },
  issueDate: { offset: 0x0167, length: 8 },
  expireDate: { offset: 0x016f, length: 8 },
  address: { offset: 0x1579, length: 100 },
} as const;

/** The JPEG photo is stored in 20 chunks of 255 bytes starting at 0x017B. */
export const THAI_ID_PHOTO = { offset: 0x017b, chunkLength: 0xff, chunks: 20 } as const;

export interface ThaiIdName {
  prefix: string;
  firstName: string;
  middleName: string;
  lastName: string;
  /** Display form, e.g. "นายสมชาย ใจดี" / "Mr. Somchai Jaidee". */
  full: string;
}

export interface ThaiIdDate {
  /** As stored on the card: YYYYMMDD, Buddhist era (พ.ศ.). */
  raw: string;
  yearBE: number | null;
  /** 1-12, null when the card stores 00 (unknown). */
  month: number | null;
  day: number | null;
  /** Gregorian date (YYYY-MM-DD) when day, month and year are all known. */
  iso: string | null;
  /** Card is valid for life (ตลอดชีพ). */
  lifelong: boolean;
}

export interface ThaiIdAddress {
  houseNo: string;
  villageNo: string;
  lane: string;
  alley: string;
  road: string;
  subdistrict: string;
  district: string;
  province: string;
  full: string;
  raw: string;
}

export type ThaiIdGender = 'male' | 'female' | 'unknown';

export interface ThaiIdCardData {
  citizenId: string;
  /** 13-digit check digit (mod 11) is correct. */
  citizenIdValid: boolean;
  nameTh: ThaiIdName;
  nameEn: ThaiIdName;
  birthDate: ThaiIdDate;
  gender: ThaiIdGender;
  issuer: string;
  issueDate: ThaiIdDate;
  expireDate: ThaiIdDate;
  address: ThaiIdAddress;
  /** JPEG bytes (null when includePhoto = false). */
  photo: Uint8Array | null;
  atr: string;
  reader: string;
  readAt: string;
}

export interface ReadProgress {
  step: string;
  done: number;
  total: number;
  percent: number;
}

export interface ReadThaiIdOptions {
  /** Read the photo too (20 extra APDU round-trips, ~5 KB). Default true. */
  includePhoto?: boolean;
  onProgress?: (progress: ReadProgress) => void;
}

export class ThaiIdCardError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = 'ThaiIdCardError';
    this.code = code;
  }
}

/** Reads everything from a Thai national ID card in `reader`. */
export async function readThaiIdCard(
  client: SmartCardBridgeClient,
  reader: string,
  options: ReadThaiIdOptions = {},
): Promise<ThaiIdCardData> {
  const includePhoto = options.includePhoto ?? true;
  const total = 1 + Object.keys(THAI_ID_FIELDS).length + (includePhoto ? THAI_ID_PHOTO.chunks : 0);
  let done = 0;
  const report = (step: string) => options.onProgress?.({ step, done, total, percent: Math.round((done / total) * 100) });
  const tick = (step: string) => {
    done++;
    report(step);
  };

  report('connect');
  const connection = await client.connect(reader, { shareMode: 'shared', protocol: 'any' });
  try {
    await client.beginTransaction(connection.handle);
    try {
      const card = new MoiApplet(client, connection);
      await card.select();
      tick('select');

      const text = async (field: keyof typeof THAI_ID_FIELDS) => {
        const { offset, length } = THAI_ID_FIELDS[field];
        const value = decodeTis620(await card.read(offset, length));
        tick(field);
        return value;
      };

      const citizenId = (await text('citizenId')).replace(/\D/g, '');
      const nameTh = parseThaiName(await text('nameTh'), 'th');
      const nameEn = parseThaiName(await text('nameEn'), 'en');
      const birthDate = parseThaiDate(await text('birthDate'));
      const genderCode = await text('gender');
      const issuer = await text('issuer');
      const issueDate = parseThaiDate(await text('issueDate'));
      const expireDate = parseThaiDate(await text('expireDate'));
      const address = parseThaiAddress(await text('address'));

      let photo: Uint8Array | null = null;
      if (includePhoto) {
        const chunks: Uint8Array[] = [];
        for (let i = 0; i < THAI_ID_PHOTO.chunks; i++) {
          chunks.push(await card.read(THAI_ID_PHOTO.offset + i * THAI_ID_PHOTO.chunkLength, THAI_ID_PHOTO.chunkLength));
          tick(`photo ${i + 1}/${THAI_ID_PHOTO.chunks}`);
        }
        photo = trimJpeg(concatBytes(chunks));
      }

      return {
        citizenId,
        citizenIdValid: isValidCitizenId(citizenId),
        nameTh,
        nameEn,
        birthDate,
        gender: genderCode === '1' ? 'male' : genderCode === '2' ? 'female' : 'unknown',
        issuer,
        issueDate,
        expireDate,
        address,
        photo,
        atr: connection.atr,
        reader,
        readAt: new Date().toISOString(),
      };
    } finally {
      await client.endTransaction(connection.handle).catch(() => undefined);
    }
  } finally {
    await client.disconnect(connection.handle).catch(() => undefined);
  }
}

/** APDU helper that hides the T=0 "61 xx → GET RESPONSE" dance. */
class MoiApplet {
  private getResponseP2: number;

  constructor(
    private readonly client: SmartCardBridgeClient,
    private readonly connection: CardConnection,
  ) {
    // Older cards (ATR 3B 67 ...) expect GET RESPONSE with P2 = 01, newer ones P2 = 00.
    const atr = fromHex(connection.atr);
    this.getResponseP2 = atr[0] === 0x3b && atr[1] === 0x67 ? 0x01 : 0x00;
  }

  async select(): Promise<void> {
    const { sw } = await this.transmit([0x00, 0xa4, 0x04, 0x00, THAI_ID_APPLET_AID.length, ...THAI_ID_APPLET_AID]);
    if (sw >> 8 !== 0x61 && sw !== 0x9000) {
      throw new ThaiIdCardError('NOT_THAI_ID_CARD', `The card has no Thai ID applet (SELECT returned ${swHex(sw)})`);
    }
  }

  async read(offset: number, length: number): Promise<Uint8Array> {
    let res = await this.transmit([0x80, 0xb0, (offset >> 8) & 0xff, offset & 0xff, 0x02, 0x00, length & 0xff]);
    if (res.sw >> 8 === 0x61) {
      res = await this.getResponse(res.sw & 0xff);
    } else if (res.sw === 0x9000 && res.data.length === 0) {
      res = await this.getResponse(length & 0xff); // some readers answer 9000 and keep the data pending
    }
    if (res.sw !== 0x9000) {
      throw new ThaiIdCardError('READ_FAILED', `Reading offset 0x${offset.toString(16).padStart(4, '0')} failed (${swHex(res.sw)})`);
    }
    return res.data;
  }

  private async getResponse(le: number): Promise<{ data: Uint8Array; sw: number }> {
    let res = await this.transmit([0x00, 0xc0, 0x00, this.getResponseP2, le]);
    if (res.sw === 0x6a86 || res.sw === 0x6b00) {
      // Wrong P1/P2: this card generation wants the other GET RESPONSE variant — switch and remember.
      this.getResponseP2 ^= 0x01;
      res = await this.transmit([0x00, 0xc0, 0x00, this.getResponseP2, le]);
    }
    if (res.sw >> 8 === 0x6c) {
      res = await this.transmit([0x00, 0xc0, 0x00, this.getResponseP2, res.sw & 0xff]);
    }
    return res;
  }

  private async transmit(apdu: readonly number[]): Promise<{ data: Uint8Array; sw: number }> {
    const response = await this.client.transmit(this.connection.handle, apdu);
    if (response.length < 2) throw new ThaiIdCardError('BAD_RESPONSE', `Short card response to ${toHex(apdu)}`);
    return {
      data: response.subarray(0, response.length - 2),
      sw: (response[response.length - 2] << 8) | response[response.length - 1],
    };
  }
}

let tis620Decoder: TextDecoder | null = null;

/** Decodes TIS-620 / windows-874 bytes and strips the space/NUL padding. */
export function decodeTis620(bytes: Uint8Array): string {
  tis620Decoder ??= new TextDecoder('windows-874');
  return tis620Decoder.decode(bytes).replace(/[\u0000\s]+$/u, '').trim();
}

export function parseThaiName(text: string, lang: 'th' | 'en'): ThaiIdName {
  const [prefix = '', firstName = '', middleName = '', lastName = ''] = text.split('#').map((s) => s.trim());
  // Thai titles are written attached to the first name ("นายสมชาย"), English ones with a space.
  const given = lang === 'th' ? `${prefix}${firstName}` : [prefix, firstName].filter(Boolean).join(' ');
  return { prefix, firstName, middleName, lastName, full: [given, middleName, lastName].filter(Boolean).join(' ') };
}

export function parseThaiDate(text: string): ThaiIdDate {
  const raw = text.trim();
  const empty: ThaiIdDate = { raw, yearBE: null, month: null, day: null, iso: null, lifelong: false };
  if (/^9{8}$/.test(raw)) return { ...empty, lifelong: true };
  if (!/^\d{8}$/.test(raw)) return empty;

  const yearBE = Number(raw.slice(0, 4));
  const month = Number(raw.slice(4, 6));
  const day = Number(raw.slice(6, 8));
  const yearCE = yearBE - 543;
  const monthOk = month >= 1 && month <= 12;
  const dayOk = monthOk && day >= 1 && day <= new Date(Date.UTC(yearCE, month, 0)).getUTCDate();
  return {
    raw,
    yearBE: yearBE > 0 ? yearBE : null,
    month: monthOk ? month : null,
    day: dayOk ? day : null,
    iso: yearBE > 0 && dayOk ? `${String(yearCE).padStart(4, '0')}-${pad2(month)}-${pad2(day)}` : null,
    lifelong: false,
  };
}

export function parseThaiAddress(text: string): ThaiIdAddress {
  const parts = text.split('#').map((s) => s.trim()).filter(Boolean);
  const address: ThaiIdAddress = {
    houseNo: '', villageNo: '', lane: '', alley: '', road: '',
    subdistrict: '', district: '', province: '',
    full: parts.join(' '),
    raw: text,
  };
  const rest: string[] = [];
  for (const part of parts) {
    if (/^(จังหวัด|กรุงเทพ)/.test(part)) address.province = part;
    else if (/^(อำเภอ|กิ่งอำเภอ|เขต)/.test(part)) address.district = part;
    else if (/^(ตำบล|แขวง)/.test(part)) address.subdistrict = part;
    else if (/^หมู่/.test(part)) address.villageNo = part;
    else if (/^ตรอก/.test(part)) address.lane = part;
    else if (/^ซอย/.test(part)) address.alley = part;
    else if (/^ถนน/.test(part)) address.road = part;
    else rest.push(part);
  }
  address.houseNo = rest.join(' ');
  return address;
}

/** Thai citizen ID check digit (mod 11). */
export function isValidCitizenId(id: string): boolean {
  if (!/^\d{13}$/.test(id)) return false;
  let sum = 0;
  for (let i = 0; i < 12; i++) sum += Number(id[i]) * (13 - i);
  return (11 - (sum % 11)) % 10 === Number(id[12]);
}

/** 1234567890121 → 1-2345-67890-12-1 */
export function formatCitizenId(id: string): string {
  return /^\d{13}$/.test(id) ? `${id[0]}-${id.slice(1, 5)}-${id.slice(5, 10)}-${id.slice(10, 12)}-${id[12]}` : id;
}

const THAI_MONTHS = [
  'มกราคม', 'กุมภาพันธ์', 'มีนาคม', 'เมษายน', 'พฤษภาคม', 'มิถุนายน',
  'กรกฎาคม', 'สิงหาคม', 'กันยายน', 'ตุลาคม', 'พฤศจิกายน', 'ธันวาคม',
];

/** "15 มกราคม 2530", "มกราคม 2530" or "2530" depending on what the card knows. */
export function formatThaiDate(date: ThaiIdDate): string {
  if (date.lifelong) return 'ตลอดชีพ';
  if (!date.yearBE) return date.raw || '-';
  const month = date.month ? THAI_MONTHS[date.month - 1] : '';
  return [date.day ?? '', month, date.yearBE].filter((part) => part !== '').join(' ');
}

/** Age in completed years (uses 1 Jan / 1st of month when the card stores 00). */
export function ageInYears(birth: ThaiIdDate, now = new Date()): number | null {
  if (!birth.yearBE) return null;
  const year = birth.yearBE - 543;
  const month = (birth.month ?? 1) - 1;
  const day = birth.day ?? 1;
  let age = now.getFullYear() - year;
  if (now.getMonth() < month || (now.getMonth() === month && now.getDate() < day)) age--;
  return age;
}

export function isExpired(expire: ThaiIdDate, now = new Date()): boolean {
  if (expire.lifelong || !expire.iso) return false;
  return new Date(`${expire.iso}T23:59:59`) < now;
}

export function photoToDataUrl(photo: Uint8Array): string {
  return `data:image/jpeg;base64,${bytesToBase64(photo)}`;
}

/** Plain JSON for sending to your backend (photo as base64 JPEG). */
export function toPayload(card: ThaiIdCardData): Record<string, unknown> {
  return { ...card, photo: card.photo ? bytesToBase64(card.photo) : null };
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

function concatBytes(chunks: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0));
  let offset = 0;
  for (const chunk of chunks) {
    out.set(chunk, offset);
    offset += chunk.length;
  }
  return out;
}

/** Cuts the padding after the JPEG end-of-image marker (FF D9). */
function trimJpeg(bytes: Uint8Array): Uint8Array {
  for (let i = bytes.length - 2; i >= 0; i--) {
    if (bytes[i] === 0xff && bytes[i + 1] === 0xd9) return bytes.slice(0, i + 2);
  }
  return bytes;
}

function swHex(sw: number): string {
  return `SW ${sw.toString(16).toUpperCase().padStart(4, '0')}`;
}

function pad2(n: number): string {
  return String(n).padStart(2, '0');
}
