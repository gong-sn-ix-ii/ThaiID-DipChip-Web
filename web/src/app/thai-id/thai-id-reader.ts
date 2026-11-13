import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ReaderStatus } from './smartcard-bridge.client';
import {
  ThaiIdCardData,
  ageInYears,
  formatCitizenId,
  formatThaiDate,
  isExpired,
  photoToDataUrl,
  toPayload,
} from './thai-id-card';
import { ThaiIdCardService } from './thai-id-card.service';

interface CardView {
  citizenId: string;
  citizenIdValid: boolean;
  nameTh: string;
  nameEn: string;
  birth: string;
  age: number | null;
  gender: string;
  address: string;
  issuer: string;
  issueDate: string;
  expireDate: string;
  expired: boolean;
  photoUrl: string | null;
  reader: string;
  readAt: string;
}

/** Drop-in UI: bridge status, reader picker, auto read, ID card view. Add ?mask=1 to the URL to start masked. */
@Component({
  selector: 'app-thai-id-reader',
  templateUrl: './thai-id-reader.html',
  styleUrl: './thai-id-reader.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThaiIdReader {
  protected readonly id = inject(ThaiIdCardService);
  protected readonly masked = signal(initialMask());
  protected readonly copied = signal(false);

  protected readonly canRead = computed(() => {
    const reader = this.id.selectedReader();
    return this.id.connection() === 'connected' && !!reader?.cardPresent && !reader.mute && !this.id.reading();
  });

  protected readonly view = computed<CardView | null>(() => {
    const card = this.id.card();
    return card ? toView(card, this.masked()) : null;
  });

  protected readonly jsonPreview = computed(() => {
    const card = this.id.card();
    if (!card) return '';
    const payload = toPayload(card);
    const photo = payload['photo'];
    if (typeof photo === 'string' && photo.length > 80) {
      payload['photo'] = `${photo.slice(0, 80)}… (${photo.length} base64 chars)`;
    }
    return JSON.stringify(payload, null, 2);
  });

  protected readerLabel(reader: ReaderStatus): string {
    if (!reader.cardPresent) return 'ไม่มีบัตร';
    if (reader.mute) return 'บัตรไม่ตอบสนอง';
    return 'มีบัตร';
  }

  protected progressLabel(step: string): string {
    if (step === 'connect') return 'กำลังเชื่อมต่อบัตร';
    if (step === 'select') return 'กำลังเปิดแอปพลิเคชันบนชิป';
    if (step.startsWith('photo')) return 'กำลังอ่านรูปถ่าย';
    return 'กำลังอ่านข้อมูล';
  }

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected async copyJson(): Promise<void> {
    const card = this.id.card();
    if (!card) return;
    try {
      await navigator.clipboard.writeText(JSON.stringify(toPayload(card), null, 2));
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    } catch {
      this.id.error.set('คัดลอกไม่สำเร็จ — เบราว์เซอร์ไม่อนุญาตให้ใช้คลิปบอร์ด');
    }
  }
}

function toView(card: ThaiIdCardData, masked: boolean): CardView {
  const birthYear = card.birthDate.yearBE;
  return {
    citizenId: masked ? maskCitizenId(card.citizenId) : formatCitizenId(card.citizenId),
    citizenIdValid: card.citizenIdValid,
    nameTh: masked ? maskName(card.nameTh.full) : card.nameTh.full || '-',
    nameEn: masked ? maskName(card.nameEn.full) : card.nameEn.full || '-',
    birth: masked ? (birthYear ? `•• •••• ${birthYear}` : '-') : formatThaiDate(card.birthDate),
    age: masked ? null : ageInYears(card.birthDate),
    gender: card.gender === 'male' ? 'ชาย' : card.gender === 'female' ? 'หญิง' : '-',
    address: masked ? ['••••••', card.address.province].filter(Boolean).join(' ') : card.address.full || '-',
    issuer: card.issuer || '-',
    issueDate: formatThaiDate(card.issueDate),
    expireDate: formatThaiDate(card.expireDate),
    expired: isExpired(card.expireDate),
    photoUrl: card.photo?.length ? photoToDataUrl(card.photo) : null,
    reader: card.reader,
    readAt: new Date(card.readAt).toLocaleString('th-TH'),
  };
}

function maskCitizenId(id: string): string {
  return /^\d{13}$/.test(id) ? `${id[0]}-xxxx-xxxxx-xx-${id[12]}` : 'x-xxxx-xxxxx-xx-x';
}

function maskName(full: string): string {
  return full.split(' ').filter(Boolean).map((word) => `${Array.from(word)[0]}•••`).join(' ') || '-';
}

function initialMask(): boolean {
  return typeof location !== 'undefined' && new URLSearchParams(location.search).get('mask') === '1';
}
