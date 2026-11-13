/**
 * End-to-end smoke test for ThaiIdBridge + the TypeScript reader, without a browser (Node 22+).
 *
 *   npm run test:bridge                                   # simulator reader if present, else first reader with a card
 *   npm run test:bridge -- --reader "ACS ACR39U ICC Reader 0"
 *   npm run test:bridge -- --show                         # print personal data unmasked (careful!)
 *
 * With the simulator (ThaiIdBridge.exe --simulate) every parsed field is asserted against the fake card.
 */
import { BridgeError, SmartCardBridgeClient } from '../src/app/thai-id/smartcard-bridge.client';
import {
  ThaiIdCardData,
  formatCitizenId,
  formatThaiDate,
  readThaiIdCard,
} from '../src/app/thai-id/thai-id-card';

declare const process: { argv: string[]; exitCode?: number };

const args = process.argv.slice(2);
const option = (name: string) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const url = option('--url') ?? 'ws://127.0.0.1:8424/ws';
const show = args.includes('--show');
let failures = 0;

function check(label: string, ok: boolean, detail = ''): void {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? `  ${detail}` : ''}`);
  if (!ok) failures++;
}

const mask = (s: string) => (show ? s : s.replace(/[^\s\-/#]/gu, (c, i: number) => (i === 0 ? c : '•')));

async function main(): Promise<void> {
  const client = new SmartCardBridgeClient(url, { autoReconnect: false });
  client.start();
  const status = await client.waitUntilReady(3000);
  console.log(`Bridge ${client.info?.app} ${client.info?.version} at ${url} (simulator: ${client.info?.simulator})`);
  console.log(`Smart Card service: ${status.service}${status.error ? ` (${status.error})` : ''}`);
  for (const r of status.readers) {
    console.log(`  reader: ${r.name}  card=${r.cardPresent}${r.mute ? ' (mute)' : ''}${r.atr ? `  ATR=${r.atr}` : ''}`);
  }

  const wanted = option('--reader');
  const reader =
    status.readers.find((r) => r.name === wanted) ??
    status.readers.find((r) => r.virtual && !wanted) ??
    status.readers.find((r) => r.cardPresent && !r.mute);
  if (!reader) {
    console.log('No reader with a card found - insert a card or start the bridge with --simulate.');
    process.exitCode = 2;
    client.stop();
    return;
  }

  if (reader.virtual && !reader.cardPresent) await client.simulator('insert');
  console.log(`\nReading "${reader.name}" ...`);
  const started = Date.now();
  let lastPercent = -1;
  const card = await readThaiIdCard(client, reader.name, {
    onProgress: (p) => {
      if (p.percent >= lastPercent + 25 || p.percent === 100) {
        lastPercent = p.percent;
        console.log(`  progress ${p.percent}% (${p.step})`);
      }
    },
  });
  console.log(`Read in ${Date.now() - started} ms\n`);
  print(card);

  console.log('\nChecks');
  check('citizen id has 13 digits', /^\d{13}$/.test(card.citizenId));
  check('citizen id check digit', card.citizenIdValid);
  check('Thai name parsed', card.nameTh.firstName.length > 0 && card.nameTh.lastName.length > 0);
  check('birth date parsed', card.birthDate.yearBE !== null, card.birthDate.raw);
  check('photo is a JPEG', !!card.photo && card.photo[0] === 0xff && card.photo[1] === 0xd8, `${card.photo?.length ?? 0} bytes`);

  if (reader.virtual) {
    check('simulator: citizen id', card.citizenId === '1234567890121');
    check('simulator: Thai name', card.nameTh.full === 'นายทดสอบ ข้อมูลจำลอง', card.nameTh.full);
    check('simulator: English name', card.nameEn.full === 'Mr. Test Simulator', card.nameEn.full);
    check('simulator: birth date', card.birthDate.iso === '1987-01-15', String(card.birthDate.iso));
    check('simulator: gender', card.gender === 'male');
    check('simulator: issuer', card.issuer === 'สำนักทะเบียนจำลอง/ทดสอบ', card.issuer);
    check('simulator: expire date', card.expireDate.iso === '2030-01-14', String(card.expireDate.iso));
    check('simulator: province', card.address.province === 'จังหวัดทดสอบ', card.address.province);
    check('simulator: house no / moo / road', card.address.houseNo === '99/9' && card.address.villageNo === 'หมู่ที่ 9' && card.address.road === 'ถนนทดสอบ');

    // Card pulled out while reading → clear error code.
    await client.simulator('remove');
    try {
      await readThaiIdCard(client, reader.name);
      check('simulator: removed card is reported', false);
    } catch (e) {
      check('simulator: removed card is reported', e instanceof BridgeError && e.code === 'SCARD_W_REMOVED_CARD', String((e as Error).message));
    }
    await client.simulator('insert');
  }

  client.stop();
  console.log(failures ? `\n${failures} check(s) FAILED` : '\nAll checks passed');
  process.exitCode = failures ? 1 : 0;
}

function print(card: ThaiIdCardData): void {
  console.log(`  Citizen ID : ${mask(formatCitizenId(card.citizenId))}`);
  console.log(`  Name (TH)  : ${mask(card.nameTh.full)}`);
  console.log(`  Name (EN)  : ${mask(card.nameEn.full)}`);
  console.log(`  Birth date : ${show ? formatThaiDate(card.birthDate) : `(year ${card.birthDate.yearBE})`}`);
  console.log(`  Gender     : ${card.gender}`);
  console.log(`  Address    : ${show ? card.address.full : `... ${card.address.province}`}`);
  console.log(`  Issuer     : ${card.issuer}`);
  console.log(`  Issued     : ${formatThaiDate(card.issueDate)}   Expires: ${formatThaiDate(card.expireDate)}`);
  console.log(`  Photo      : ${card.photo?.length ?? 0} bytes   ATR: ${card.atr}`);
}

main().catch((e: unknown) => {
  console.error('ERROR', e instanceof Error ? `${e.name}: ${e.message}` : e);
  process.exitCode = 1;
});
