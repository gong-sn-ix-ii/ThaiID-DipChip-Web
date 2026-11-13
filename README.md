# Thai ID Dip Chip บน Windows (Angular + TypeScript)

อ่านบัตรประชาชนแบบ Dip Chip จากเว็บแอป Angular บน Windows ใช้ได้กับเครื่องอ่าน **TDC39VC (USB Type‑C)** และเครื่องอ่าน Smart Card แบบ CCID ทั่วไป

## สรุปสั้น ๆ

- **ทำได้จริง** ทดสอบครบทั้งเส้นทางบนเครื่องนี้แล้ว (เว็บ → bridge → PC/SC → บัตร) โดยใช้บัตรจำลอง
- **แต่ใช้เว็บอย่างเดียว 100% บน Windows ไม่ได้** ต้องมีโปรแกรมตัวเล็ก `ThaiIdBridge.exe` (62 KB) ไม่ต้องติดตั้ง ดับเบิลคลิกแล้วใช้ได้เลย ทำหน้าที่เป็น "สะพาน" ระหว่างเบราว์เซอร์กับเครื่องอ่านบัตร
- TDC39VC ที่เคยเสียบกับเครื่องนี้ Windows มองเห็นเป็น `Microsoft Usbccid Smartcard Reader (WUDF)` (USB VID `072F` = ACS ตระกูล ACR39U) แปลว่าเป็นเครื่องอ่านมาตรฐาน CCID ใช้ไดรเวอร์ที่มากับ Windows ได้เลย ไม่ต้องลงไดรเวอร์เพิ่ม
- โค้ดอ่านบัตร (คำสั่ง APDU และการแปลงข้อมูล) เขียนเป็น TypeScript ทั้งหมด ส่วน bridge เป็นแค่ตัวส่งต่อ PC/SC ทั่วไป ถ้าแก้วิธีอ่านบัตรก็ไม่ต้องแจก exe ใหม่

## ทำไมเว็บล้วน ๆ อ่านบัตรบน Windows ไม่ได้

| ทางเลือก | ผล |
|---|---|
| WebUSB | Chrome/Edge บล็อก USB class Smart Card (`0x0B`) และบน Windows เครื่องอ่านถูกไดรเวอร์ CCID ของ Windows จองไว้แล้ว (WebUSB ใช้ได้เฉพาะอุปกรณ์ที่ใช้ไดรเวอร์ WinUSB) |
| Web Smart Card API (`navigator.smartCard`) | มีใน Chrome 143 แล้ว แต่**ใช้ได้เฉพาะ Isolated Web App** ที่ต้องติดตั้งและจัดการผ่าน policy ขององค์กร เว็บทั่วไปเรียกใช้ไม่ได้ |
| WebHID / Web Serial | เครื่องอ่านบัตรไม่ใช่อุปกรณ์ประเภทนี้ |
| **Local bridge (วิธีที่ใช้ในโปรเจกต์นี้)** | ✅ ใช้ได้กับ Chrome/Edge ปัจจุบัน และเป็นแนวทางที่ระบบเว็บที่ต้องอ่านบัตรประชาชนใช้กันทั่วไป |

## สถาปัตยกรรม

```
┌───────────────────────────┐  ws://127.0.0.1:8424/ws  ┌────────────────────────┐  PC/SC (winscard.dll)  ┌───────────┐
│ Angular web app           │ ───────────────────────▶ │ ThaiIdBridge.exe       │ ─────────────────────▶ │ TDC39VC   │ ──▶ บัตรประชาชน
│ https://ระบบของคุณ         │ ◀──── JSON (APDU/สถานะ) ─ │ รันบนเครื่องที่เสียบเครื่องอ่าน │                        │ (CCID)    │
└───────────────────────────┘                          └────────────────────────┘                        └───────────┘
```

- bridge รับการเชื่อมต่อเฉพาะจาก `127.0.0.1` (เครื่องเดียวกัน) และส่งสถานะเครื่องอ่าน/บัตรมาให้เว็บแบบ real-time (เสียบ/ถอดบัตร)
- เว็บส่งคำสั่ง APDU ผ่าน bridge แล้วแปลงข้อมูลเองใน TypeScript ได้แก่ TIS‑620, วันที่ พ.ศ., ที่อยู่, รูปถ่าย JPEG และเลขตรวจสอบ 13 หลัก

## เริ่มใช้งานทันที

1. เสียบเครื่องอ่าน TDC39VC แล้วรอให้ Windows ลงไดรเวอร์ให้เอง
2. ดับเบิลคลิก `bridge\dist\ThaiIdBridge.exe` หน้าต่างคอนโซลจะแสดงชื่อเครื่องอ่านที่เจอ
3. เปิด **http://127.0.0.1:8424** ใน Chrome หรือ Edge แล้วเสียบบัตร ข้อมูลและรูปจะขึ้นอัตโนมัติ

ถ้าตอนพัฒนายังไม่มีเครื่องอ่านหรือบัตร ให้รัน `ThaiIdBridge.exe --simulate` จะได้เครื่องอ่านจำลองพร้อมบัตรปลอม กดปุ่มเสียบ/ถอดบัตรจำลองได้จากหน้าเว็บ

ไฟล์ที่ต้องแจกไปเครื่องอื่นมีแค่โฟลเดอร์ `bridge\dist` (หรือ `release\ThaiIdBridge-1.0.0-portable.zip`) ข้างในมี exe, ไฟล์ config และหน้าเว็บ (`www`)

## ใส่เข้าโปรเจกต์ Angular ของทีม

คัดลอกโฟลเดอร์ `web/src/app/thai-id/` ไปใส่ในโปรเจกต์ (Angular 17 ขึ้นไป เพราะใช้ signals และ control flow ใหม่)

| ไฟล์ | หน้าที่ |
|---|---|
| `smartcard-bridge.client.ts` | WebSocket client สำหรับคุยกับ bridge เชื่อมต่อใหม่ให้อัตโนมัติ ใช้ได้กับทุก framework และ Node |
| `thai-id-card.ts` | คำสั่ง APDU ของบัตรประชาชนและตัวแปลงข้อมูลทั้งหมด (ไม่ผูกกับ Angular) |
| `thai-id-card.service.ts` | Angular service ที่ให้สถานะ bridge, เครื่องอ่าน, บัตรล่าสุด และ error เป็น signals พร้อมอ่านอัตโนมัติเมื่อเสียบบัตร |
| `thai-id-reader.*` | UI สำเร็จรูป `<app-thai-id-reader />` |

ตัวอย่างการใช้แค่ service:

```ts
import { Component, effect, inject } from '@angular/core';
import { ThaiIdCardService } from './thai-id/thai-id-card.service';
import { toPayload } from './thai-id/thai-id-card';

@Component({
  selector: 'app-register',
  template: `
    @if (id.card(); as c) { <p>{{ c.nameTh.full }} ({{ c.citizenId }})</p> }
    @if (id.error(); as e) { <p class="error">{{ e }}</p> }
    <button (click)="id.read()" [disabled]="id.reading()">อ่านบัตร</button>
  `,
})
export class RegisterPage {
  readonly id = inject(ThaiIdCardService);

  constructor() {
    effect(() => {
      const card = this.id.card();
      if (card) {
        // this.http.post('/api/customers', toPayload(card)).subscribe();  // รูปถูกแปลงเป็น base64 ให้แล้ว
      }
    });
  }
}
```

ถ้า bridge ใช้พอร์ตอื่น ให้ตั้งค่าใน `app.config.ts` ว่า `{ provide: THAI_ID_BRIDGE_URL, useValue: 'ws://127.0.0.1:9000/ws' }`

ข้อมูลที่ได้ (`ThaiIdCardData`) ได้แก่ `citizenId`, `citizenIdValid`, `nameTh` / `nameEn` (prefix, firstName, middleName, lastName, full), `birthDate` / `issueDate` / `expireDate` (raw พ.ศ., ISO ค.ศ., ตลอดชีพ), `gender`, `issuer`, `address` (บ้านเลขที่, หมู่, ตรอก, ซอย, ถนน, ตำบล, อำเภอ, จังหวัด), `photo` (JPEG) และ `atr`

## ขึ้น production (เว็บอยู่บนโดเมนจริง)

1. **ใส่โดเมนเว็บใน allowlist** ที่ `ThaiIdBridge.config.json` ซึ่งอยู่ข้าง exe แล้วเปิด exe ใหม่ bridge จะปฏิเสธทุกเว็บที่ไม่อยู่ในรายการ เพื่อกันไม่ให้เว็บอื่นแอบอ่านบัตรที่เสียบค้างไว้
   ```json
   { "port": 8424, "allowedOrigins": ["https://register.yourcompany.co.th"] }
   ```
2. **เว็บต้องเป็น HTTPS** Chrome ยอมให้หน้า https ต่อ `ws://127.0.0.1` ได้ ไม่นับเป็น mixed content แต่หน้า http:// ที่ไม่ใช่ localhost จะถูกปฏิเสธสิทธิ์นี้อัตโนมัติ
3. **ครั้งแรก Chrome/Edge จะถามสิทธิ์ "Loopback network"** (Local Network Access ซึ่งบังคับใช้กับ WebSocket ตั้งแต่ราว Chrome 147) ให้ผู้ใช้กด Allow ครั้งเดียวต่อเว็บ
   - เครื่องขององค์กร: ตั้ง enterprise policy `LocalNetworkAccessAllowedForUrls` ใส่โดเมนเว็บไว้ เพื่อข้ามคำถามนี้ (ดูรูปแบบค่าในเอกสาร Chrome Enterprise)
   - ถ้าฝังหน้าอ่านบัตรไว้ใน iframe ต้องใส่ `allow="local-network; loopback-network"`
4. ตอนพัฒนาด้วย `ng serve` (http://localhost:4200) จะไม่มีคำถามสิทธิ์ และ bridge อนุญาต localhost ทุกพอร์ตอยู่แล้ว

ถ้าอยากให้ bridge เปิดเองทุกครั้งที่เข้า Windows: กด Win+R พิมพ์ `shell:startup` แล้ววาง shortcut ของ `ThaiIdBridge.exe` ไว้ในโฟลเดอร์นั้น (ตั้ง Run เป็น Minimized)

### ตัวเลือกของ bridge

| ตัวเลือก (command line) | ใน config JSON | ความหมาย |
|---|---|---|
| `--port 8424` | `port` | พอร์ตบน 127.0.0.1 |
| `--allow-origin https://app.example.com` | `allowedOrigins` | เว็บที่อนุญาต (ใส่ซ้ำได้ และใช้ `https://*.example.com` ได้) |
| `--no-localhost-origins` | `allowLocalhostOrigins: false` | ไม่อนุญาตหน้า localhost/127.0.0.1 อัตโนมัติ |
| `--www <dir>` | `wwwRoot` | โฟลเดอร์หน้าเว็บที่ให้ bridge เสิร์ฟที่ `/` (ค่าเริ่มต้น `www`) |
| `--simulate` | `simulate` | เพิ่มเครื่องอ่านจำลองพร้อมบัตรปลอม |
| `--verbose` | `verbose` | log คำสั่ง APDU และ status word เท่านั้น (ไม่ log ข้อมูลในบัตร) |

## Protocol ของ bridge (เผื่อเขียน client ภาษาอื่น)

WebSocket `ws://127.0.0.1:8424/ws` รับส่ง JSON

```
→ {"id":1,"method":"connect","params":{"reader":"ACS ACR39U ICC Reader 0","shareMode":"shared","protocol":"any"}}
← {"id":1,"result":{"handle":"1","protocol":"T0","atr":"3B68..."}}
→ {"id":2,"method":"transmit","params":{"handle":"1","apdu":"00A4040008A000000054480001"}}
← {"id":2,"result":{"response":"610A"}}
← {"event":"status","data":{"service":"ready","readers":[{"name":"...","cardPresent":true,"mute":false,"atr":"3B68..."}]}}
```

method ที่รองรับ: `listReaders`, `getStatus`, `connect`, `transmit`, `beginTransaction`, `endTransaction`, `disconnect`, `ping`, `simulator` (ใช้ได้เฉพาะตอนเปิด `--simulate`) ส่วน HTTP มี `GET /api/status` และ `POST /api/simulator/insert|remove|toggle`

## คำสั่งอ่านบัตร (สำหรับเทียบกับฝั่ง Android/iOS)

SELECT applet `00 A4 04 00 08 A0 00 00 00 54 48 00 01` แล้วอ่านแต่ละช่องด้วย `80 B0 <offset 2 ไบต์> 02 00 <length>` ตามด้วย `00 C0 00 <P2> <length>` (GET RESPONSE: บัตรที่ ATR ขึ้นต้นด้วย `3B 67` ใช้ P2 = `01` บัตรอื่นใช้ `00`)

| ข้อมูล | offset | length |
|---|---|---|
| เลขประจำตัวประชาชน | `0x0004` | 13 |
| ชื่อไทย (`คำนำหน้า#ชื่อ#ชื่อกลาง#นามสกุล`) | `0x0011` | 100 |
| ชื่ออังกฤษ | `0x0075` | 100 |
| วันเกิด (YYYYMMDD พ.ศ.) | `0x00D9` | 8 |
| เพศ (1 = ชาย, 2 = หญิง) | `0x00E1` | 1 |
| ผู้ออกบัตร | `0x00F6` | 100 |
| วันออกบัตร | `0x0167` | 8 |
| วันหมดอายุ (`99999999` = ตลอดชีพ) | `0x016F` | 8 |
| ที่อยู่ (แยกด้วย `#`) | `0x1579` | 100 |
| รูปถ่าย JPEG (20 ส่วน ส่วนละ 255 ไบต์) | `0x017B` + n × `0xFF` | 255 |

## ความปลอดภัย

- bridge ฟังเฉพาะ `127.0.0.1` / `[::1]` เครื่องอื่นในเครือข่ายเชื่อมต่อเข้ามาไม่ได้
- ตรวจ `Origin` ทุกครั้งที่เปิด WebSocket โดยรับเฉพาะเว็บใน allowlist และ localhost
- ตรวจ `Host` header เพื่อกัน DNS rebinding และกัน path traversal ในการเสิร์ฟไฟล์
- ไม่บันทึกข้อมูลในบัตรลง log หรือดิสก์ ข้อมูลอยู่ในหน่วยความจำของหน้าเว็บเท่านั้นจนกว่าระบบของคุณจะส่งเอง
- แต่ละ client มี PC/SC context ของตัวเอง และอ่านบัตรภายใน transaction เพื่อไม่ให้หลายแท็บอ่านซ้อนกันจนข้อมูลเพี้ยน

## Build และทดสอบเอง

- **Bridge**: รัน `bridge\build.cmd` ซึ่งใช้ C# compiler ที่มากับ Windows (.NET Framework 4.8) ไม่ต้องลง Visual Studio หรือ .NET SDK จะได้ `bridge\dist\ThaiIdBridge.exe` และถ้า build เว็บไว้แล้ว จะ copy หน้าเว็บเข้า `dist\www` ให้ด้วย
- **Web**: `cd web` → `npm install` → `npm run build` (ตอนพัฒนาใช้ `npm start` แล้วเปิด http://localhost:4200)
- **ทดสอบทั้งระบบแบบไม่ต้องเปิดเบราว์เซอร์**: รัน `ThaiIdBridge.exe --simulate` แล้วรัน `npm run test:bridge` ในโฟลเดอร์ `web` กับบัตรจริงใช้ `npm run test:bridge -- --reader "<ชื่อเครื่องอ่าน>"` ข้อมูลส่วนบุคคลจะถูกปิดบัง ยกเว้นใส่ `--show`
- ใส่ `?mask=1` ต่อท้าย URL หน้าเว็บเพื่อเปิดโหมดปิดบังข้อมูลบนจอ (เหมาะกับการเดโม)

## แก้ปัญหา

| อาการ | วิธีแก้ |
|---|---|
| หน้าเว็บขึ้น "ไม่พบ Thai ID Bridge" | ยังไม่ได้เปิด exe, พอร์ตไม่ตรง หรือ Chrome บล็อกสิทธิ์ Loopback network (ดูที่ไอคอนหน้า URL → Site settings) |
| log ของ bridge ขึ้น `Rejected WebSocket from origin ...` | เพิ่ม origin นั้นใน `allowedOrigins` แล้วเปิด exe ใหม่ |
| "ไม่พบเครื่องอ่านบัตร" | เปิด Device Manager → Smart card readers ต้องเห็น Microsoft Usbccid Smartcard Reader ถ้าไม่เห็นให้ลองพอร์ตหรือหัวแปลงอื่น |
| "บัตรไม่ตอบสนอง" | บัตรเสียบกลับด้าน หรือหน้าชิปสกปรก |
| "เครื่องอ่านบัตรถูกโปรแกรมอื่นใช้งานอยู่" | ปิดโปรแกรมอ่านบัตรตัวอื่นที่จองเครื่องอ่านแบบ exclusive |
| Windows แจ้งว่า "ติดตั้งไดรเวอร์ Smart card ไม่สำเร็จ" ตอนเสียบบัตร | ไม่มีผลกับการอ่าน เกิดจาก Windows พยายามหา minidriver ให้บัตร ปิดได้ด้วย Group Policy "Turn on Smart Card Plug and Play service" = Disabled |
| `Port 8424 is already in use` | มี bridge เปิดอยู่แล้ว หรือเปลี่ยนไปใช้ `--port` อื่น (แล้วตั้ง `THAI_ID_BRIDGE_URL` ในแอปให้ตรงกัน) |

## สิ่งที่ยังต้องทำก่อนใช้งานจริงหลายเครื่อง

- **ทดสอบกับบัตรจริง**: ตอนพัฒนายังไม่ได้เสียบเครื่องอ่าน จึงทดสอบด้วยบัตรจำลองเท่านั้น ให้ทดสอบตามหัวข้อ "เริ่มใช้งานทันที"
- เซ็นโค้ด (code signing) ให้ exe เพื่อไม่ให้ SmartScreen เตือนเวลาดาวน์โหลดจากอินเทอร์เน็ต
- ถ้าต้องติดตั้งหลายเครื่อง ควรทำเป็น tray app หรือ Windows service พร้อม installer (MSI หรือแจกผ่าน Intune/GPO)

## โครงสร้างไฟล์

```
bridge/
  src/                    โค้ด C# ของ bridge (PC/SC, WebSocket, simulator)
  build.cmd               build ด้วย csc ที่มากับ Windows
  ThaiIdBridge.config.json
  dist/                   ThaiIdBridge.exe + config + www (พร้อมแจก)
web/                      Angular 21 app
  src/app/thai-id/        ส่วนที่นำไปใช้ในโปรเจกต์อื่นได้ (client, reader, service, component)
  scripts/bridge-smoke-test.ts
release/ThaiIdBridge-1.0.0-portable.zip
```
