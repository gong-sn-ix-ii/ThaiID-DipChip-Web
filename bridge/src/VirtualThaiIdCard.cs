using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;

namespace ThaiIdBridge
{
    /// <summary>
    /// Simulated reader + Thai national ID card (MOI applet, T=0 style 61xx/GET RESPONSE) so the web app can be
    /// developed and tested without hardware. Contains FAKE data only. Enable with --simulate.
    /// </summary>
    internal sealed class VirtualThaiIdCard
    {
        public const string ReaderName = "ThaiIdBridge Virtual Reader 0";

        private const int PhotoOffset = 0x017B;
        private const int PhotoLength = 20 * 255;
        private static readonly byte[] CardAtr = { 0x3B, 0x68, 0x00, 0x00, 0x00, 0x73, 0xC8, 0x40, 0x12, 0x00, 0x90, 0x00 };
        private static readonly byte[] MoiAid = { 0xA0, 0x00, 0x00, 0x00, 0x54, 0x48, 0x00, 0x01 };

        private readonly object _sync = new object();
        private readonly byte[] _memory = new byte[0x1700];
        private bool _inserted = true;
        private int _eventCount;
        private int _connections;
        private bool _selected;
        private byte[] _pending;

        public VirtualThaiIdCard()
        {
            Encoding tis620 = Encoding.GetEncoding(874);
            for (int i = 0; i < _memory.Length; i++) _memory[i] = 0x20;
            Put(tis620, 0x0004, 13, "1234567890121");
            Put(tis620, 0x0011, 100, "นาย#ทดสอบ##ข้อมูลจำลอง");
            Put(tis620, 0x0075, 100, "Mr.#Test##Simulator");
            Put(tis620, 0x00D9, 8, "25300115");
            Put(tis620, 0x00E1, 1, "1");
            Put(tis620, 0x00F6, 100, "สำนักทะเบียนจำลอง/ทดสอบ");
            Put(tis620, 0x0167, 8, "25650101");
            Put(tis620, 0x016F, 8, "25730114");
            Put(tis620, 0x1579, 100, "99/9#หมู่ที่ 9###ถนนทดสอบ#ตำบลตัวอย่าง#อำเภอจำลอง#จังหวัดทดสอบ");

            Array.Clear(_memory, PhotoOffset, PhotoLength);
            byte[] photo = CreatePhoto(PhotoLength);
            Buffer.BlockCopy(photo, 0, _memory, PhotoOffset, Math.Min(photo.Length, PhotoLength));
        }

        public bool Inserted
        {
            get { lock (_sync) { return _inserted; } }
        }

        public void Insert()
        {
            lock (_sync)
            {
                if (_inserted) return;
                _inserted = true;
                _eventCount++;
                _selected = false;
                _pending = null;
            }
            Log.Info("Simulator: card inserted");
        }

        public void Remove()
        {
            lock (_sync)
            {
                if (!_inserted) return;
                _inserted = false;
                _eventCount++;
                _selected = false;
                _pending = null;
            }
            Log.Info("Simulator: card removed");
        }

        public void Toggle()
        {
            if (Inserted) Remove();
            else Insert();
        }

        public void Connect()
        {
            lock (_sync)
            {
                if (!_inserted) throw new PcscException("SCardConnect", Pcsc.SCARD_W_REMOVED_CARD);
                _connections++;
            }
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                if (_connections > 0) _connections--;
            }
        }

        public byte[] GetAtr()
        {
            return (byte[])CardAtr.Clone();
        }

        public ReaderInfo GetReaderInfo()
        {
            lock (_sync)
            {
                var info = new ReaderInfo();
                info.Name = ReaderName;
                info.IsVirtual = true;
                info.EventCount = _eventCount;
                info.State = _inserted ? Pcsc.SCARD_STATE_PRESENT : Pcsc.SCARD_STATE_EMPTY;
                if (_inserted && _connections > 0) info.State |= Pcsc.SCARD_STATE_INUSE;
                info.Atr = _inserted ? (byte[])CardAtr.Clone() : new byte[0];
                return info;
            }
        }

        public byte[] Transmit(byte[] apdu)
        {
            lock (_sync)
            {
                if (!_inserted) throw new PcscException("SCardTransmit", Pcsc.SCARD_W_REMOVED_CARD);
                Thread.Sleep(2); // a little card latency
                if (apdu.Length < 4) return Sw(0x6700);
                byte cla = apdu[0], ins = apdu[1], p1 = apdu[2], p2 = apdu[3];

                if (cla == 0x00 && ins == 0xA4) // SELECT by AID
                {
                    _pending = null;
                    if (p1 == 0x04 && apdu.Length >= 5 + MoiAid.Length && apdu[4] == MoiAid.Length && HasAid(apdu, 5))
                    {
                        _selected = true;
                        _pending = new byte[] { 0x6F, 0x08, 0x84, 0x06, 0xA0, 0x00, 0x00, 0x00, 0x54, 0x48 };
                        return Sw(0x610A);
                    }
                    _selected = false;
                    return Sw(0x6A82);
                }

                if (cla == 0x00 && ins == 0xC0) // GET RESPONSE
                {
                    if (_pending == null) return Sw(0x6985);
                    int le = apdu.Length >= 5 ? (apdu[4] == 0 ? 256 : apdu[4]) : 256;
                    if (le > _pending.Length) return Sw(0x6C00 | (_pending.Length & 0xFF));
                    byte[] data = _pending;
                    _pending = null;
                    var response = new byte[le + 2];
                    Buffer.BlockCopy(data, 0, response, 0, le);
                    response[le] = 0x90;
                    response[le + 1] = 0x00;
                    return response;
                }

                if (cla == 0x80 && ins == 0xB0) // MOI read: 80 B0 <offset hi> <offset lo> 02 00 <length>
                {
                    _pending = null;
                    if (!_selected) return Sw(0x6986);
                    if (apdu.Length != 7 || apdu[4] != 0x02 || apdu[5] != 0x00) return Sw(0x6700);
                    int offset = (p1 << 8) | p2;
                    int length = apdu[6] == 0 ? 256 : apdu[6];
                    if (offset + length > _memory.Length) return Sw(0x6B00);
                    _pending = new byte[length];
                    Buffer.BlockCopy(_memory, offset, _pending, 0, length);
                    return Sw(0x6100 | (length & 0xFF));
                }

                _pending = null;
                return Sw(0x6D00);
            }
        }

        private static bool HasAid(byte[] apdu, int start)
        {
            for (int i = 0; i < MoiAid.Length; i++)
            {
                if (apdu[start + i] != MoiAid[i]) return false;
            }
            return true;
        }

        private static byte[] Sw(int sw)
        {
            return new[] { (byte)(sw >> 8), (byte)sw };
        }

        private void Put(Encoding encoding, int offset, int length, string text)
        {
            byte[] bytes = encoding.GetBytes(text);
            if (bytes.Length > length) throw new InvalidOperationException("Simulator field too long at 0x" + offset.ToString("X4"));
            Buffer.BlockCopy(bytes, 0, _memory, offset, bytes.Length);
            for (int i = bytes.Length; i < length; i++) _memory[offset + i] = 0x20;
        }

        /// <summary>Draws a placeholder portrait and encodes it as a JPEG that fits the card's photo area.</summary>
        private static byte[] CreatePhoto(int maxBytes)
        {
            try
            {
                using (var bitmap = new Bitmap(180, 216))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.FromArgb(206, 222, 240));
                        using (var shirt = new SolidBrush(Color.FromArgb(52, 78, 120)))
                        using (var skin = new SolidBrush(Color.FromArgb(232, 196, 166)))
                        using (var hair = new SolidBrush(Color.FromArgb(38, 38, 46)))
                        {
                            g.FillEllipse(shirt, 18, 150, 144, 140);
                            g.FillRectangle(skin, 78, 120, 24, 36);
                            g.FillEllipse(skin, 52, 44, 76, 92);
                            g.FillPie(hair, 49, 36, 82, 64, 180, 180);
                        }
                        using (var font = new Font("Arial", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var text = new SolidBrush(Color.White))
                        using (var format = new StringFormat())
                        {
                            format.Alignment = StringAlignment.Center;
                            g.DrawString("SIMULATED CARD", font, text, new RectangleF(0, 192, 180, 18), format);
                        }
                    }

                    ImageCodecInfo jpeg = null;
                    foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                    {
                        if (codec.FormatID == ImageFormat.Jpeg.Guid) jpeg = codec;
                    }
                    if (jpeg == null) return new byte[0];

                    for (long quality = 75; quality >= 15; quality -= 10)
                    {
                        using (var stream = new MemoryStream())
                        using (var parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                            bitmap.Save(stream, jpeg, parameters);
                            if (stream.Length <= maxBytes) return stream.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Simulator photo could not be generated: " + ex.Message);
            }
            return new byte[0];
        }
    }
}
