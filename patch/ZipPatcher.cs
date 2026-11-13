using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

/// <summary>
/// Surgical ZIP patcher: replaces ONE small entry inside an existing ZIP (incl. ZIP64)
/// while copying every other byte of the archive verbatim, and correctly shifting the
/// central-directory local-header offsets of entries that sit after the patched one.
/// </summary>
public static class ZipPatcher
{
    class Central
    {
        public string Name;
        public uint Flags;
        public ushort Method;
        public long StdOffPos;      // position in CD buffer of the 4-byte local offset
        public long CrcPos, CSizePos, USizePos;
        public uint LocalOffset;    // standard field value (0xFFFFFFFF when in zip64 extra)
        public long Zip64OffPos = -1, Zip64CSizePos = -1, Zip64USizePos = -1;
        public long FullLen;
        public long EffOffset;      // real local header offset (from zip64 extra if present)
    }

    static byte[] ReadBytes(Stream s, int n)
    {
        byte[] b = new byte[n];
        int o = 0;
        while (o < n)
        {
            int r = s.Read(b, o, n - o);
            if (r <= 0) throw new EndOfStreamException();
            o += r;
        }
        return b;
    }

    static void CopyRange(Stream src, Stream dst, long from, long to, byte[] buf)
    {
        src.Seek(from, SeekOrigin.Begin);
        long remaining = to - from;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int r = src.Read(buf, 0, want);
            if (r <= 0) throw new EndOfStreamException("copy");
            dst.Write(buf, 0, r);
            remaining -= r;
        }
    }

    static uint[] CrcTable;
    static uint Crc32(byte[] data)
    {
        if (CrcTable == null)
        {
            CrcTable = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                CrcTable[i] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Public wrapper so verification scripts can CRC their own data.</summary>
    public static uint Crc32Of(byte[] data)
    {
        return Crc32(data);
    }

    /// <summary>
    /// Returns [central-directory CRC32, local-header CRC32] for one entry.
    /// The central directory is authoritative even for streamed (data-descriptor)
    /// entries; comparing both against Crc32Of(content) proves the archive is
    /// self-consistent. .NET Framework's ZipArchiveEntry exposes no Crc32
    /// property, so the verifier needs this. (zip64 CD offsets not supported -
    /// small entries like package.prop never use them.)
    /// </summary>
    public static uint[] ReadStoredCrcs(string zipPath, string targetEntry)
    {
        using (var fs = File.OpenRead(zipPath))
        {
            long fileLen = fs.Length;
            int tailLen = (int)Math.Min(fileLen, 70000);
            fs.Seek(fileLen - tailLen, SeekOrigin.Begin);
            byte[] tail = ReadBytes(fs, tailLen);
            int eocdIdx = -1;
            for (int i = tailLen - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocdIdx = i; break; }
            if (eocdIdx < 0) throw new Exception("EOCD not found");
            long eocdOff = fileLen - tailLen + eocdIdx;

            long cdOffset, cdSize;
            fs.Seek(eocdOff - 20, SeekOrigin.Begin);
            byte[] loc = ReadBytes(fs, 20);
            if (loc[0] == 0x50 && loc[1] == 0x4b && loc[2] == 0x06 && loc[3] == 0x07)
            {
                fs.Seek((long)BitConverter.ToUInt64(loc, 8), SeekOrigin.Begin);
                byte[] z64 = ReadBytes(fs, 56);
                if (!(z64[0] == 0x50 && z64[1] == 0x4b && z64[2] == 0x06 && z64[3] == 0x06))
                    throw new Exception("bad ZIP64 EOCD");
                cdSize = (long)BitConverter.ToUInt64(z64, 40);
                cdOffset = (long)BitConverter.ToUInt64(z64, 48);
            }
            else
            {
                cdSize = BitConverter.ToUInt32(tail, eocdIdx + 12);
                cdOffset = BitConverter.ToUInt32(tail, eocdIdx + 16);
            }

            fs.Seek(cdOffset, SeekOrigin.Begin);
            byte[] cd = ReadBytes(fs, (int)cdSize);
            int p = 0;
            while (p + 46 <= cd.Length)
            {
                if (!(cd[p] == 0x50 && cd[p + 1] == 0x4b && cd[p + 2] == 0x01 && cd[p + 3] == 0x02))
                    throw new Exception("bad CD signature at " + p);
                ushort nl = BitConverter.ToUInt16(cd, p + 28);
                ushort el = BitConverter.ToUInt16(cd, p + 30);
                ushort cl = BitConverter.ToUInt16(cd, p + 32);
                string name = Encoding.UTF8.GetString(cd, p + 46, nl);
                if (name == targetEntry)
                {
                    uint cdCrc = BitConverter.ToUInt32(cd, p + 16);
                    ulong off = BitConverter.ToUInt32(cd, p + 42);
                    if (off == 0xFFFFFFFFu) throw new Exception("zip64 entry offsets unsupported by this helper");
                    fs.Seek((long)off, SeekOrigin.Begin);
                    byte[] lfh = ReadBytes(fs, 30);
                    if (!(lfh[0] == 0x50 && lfh[1] == 0x4b && lfh[2] == 0x03 && lfh[3] == 0x04))
                        throw new Exception("bad local header for " + targetEntry);
                    uint lfhCrc = BitConverter.ToUInt32(lfh, 14);
                    return new uint[] { cdCrc, lfhCrc };
                }
                p += 46 + nl + el + cl;
            }
            throw new Exception("entry not found: " + targetEntry);
        }
    }

    static byte[] Inflate(ushort method, byte[] comp)
    {
        if (method == 0) return comp;
        if (method != 8) throw new Exception("unsupported compression method " + method);
        using (var ms = new MemoryStream(comp))
        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }
    }

    static void W32(byte[] b, long pos, uint v)
    {
        b[pos] = (byte)v; b[pos + 1] = (byte)(v >> 8); b[pos + 2] = (byte)(v >> 16); b[pos + 3] = (byte)(v >> 24);
    }
    static void W64(byte[] b, long pos, ulong v)
    {
        for (int i = 0; i < 8; i++) b[pos + i] = (byte)(v >> (8 * i));
    }

    public static string Patch(string src, string dst, string targetEntry, byte[] newData)
    {
        var log = new StringBuilder();
        using (var fs = File.OpenRead(src))
        {
            long fileLen = fs.Length;

            // ---- locate End Of Central Directory (and ZIP64 structures) ----
            int tailLen = (int)Math.Min(fileLen, 70000);
            fs.Seek(fileLen - tailLen, SeekOrigin.Begin);
            byte[] tail = ReadBytes(fs, tailLen);
            int eocdIdx = -1;
            for (int i = tailLen - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocdIdx = i; break; }
            if (eocdIdx < 0) throw new Exception("EOCD not found");
            long eocdOff = fileLen - tailLen + eocdIdx;

            bool zip64 = false; long zip64Off = -1;
            if (eocdOff >= 20)
            {
                fs.Seek(eocdOff - 20, SeekOrigin.Begin);
                byte[] loc = ReadBytes(fs, 20);
                if (loc[0] == 0x50 && loc[1] == 0x4b && loc[2] == 0x06 && loc[3] == 0x07)
                {
                    zip64 = true;
                    zip64Off = (long)BitConverter.ToUInt64(loc, 8);
                }
            }

            long cdOffset, cdSize, entryCount;
            if (zip64)
            {
                fs.Seek(zip64Off, SeekOrigin.Begin);
                byte[] z64 = ReadBytes(fs, 56);
                if (!(z64[0] == 0x50 && z64[1] == 0x4b && z64[2] == 0x06 && z64[3] == 0x06)) throw new Exception("bad ZIP64 EOCD");
                entryCount = (long)BitConverter.ToUInt64(z64, 32);
                cdSize = (long)BitConverter.ToUInt64(z64, 40);
                cdOffset = (long)BitConverter.ToUInt64(z64, 48);
                log.AppendLine("ZIP64 archive: entries=" + entryCount + " cdOffset=" + cdOffset + " cdSize=" + cdSize);
            }
            else
            {
                entryCount = BitConverter.ToUInt16(tail, eocdIdx + 10);
                cdSize = BitConverter.ToUInt32(tail, eocdIdx + 12);
                cdOffset = BitConverter.ToUInt32(tail, eocdIdx + 16);
                log.AppendLine("Plain ZIP: entries=" + entryCount + " cdOffset=" + cdOffset + " cdSize=" + cdSize);
            }
            if (cdOffset + cdSize > fileLen) throw new Exception("central directory out of range");

            // ---- parse central directory ----
            fs.Seek(cdOffset, SeekOrigin.Begin);
            byte[] cd = ReadBytes(fs, (int)cdSize);
            var centrals = new List<Central>();
            int p = 0;
            while (p < cd.Length)
            {
                if (!(cd[p] == 0x50 && cd[p + 1] == 0x4b && cd[p + 2] == 0x01 && cd[p + 3] == 0x02)) throw new Exception("bad CD signature at " + p);
                var c = new Central();
                c.Flags = BitConverter.ToUInt16(cd, p + 8);
                c.Method = BitConverter.ToUInt16(cd, p + 10);
                c.CrcPos = p + 16; c.CSizePos = p + 20; c.USizePos = p + 24;
                ushort nl = BitConverter.ToUInt16(cd, p + 28);
                ushort el = BitConverter.ToUInt16(cd, p + 30);
                ushort cl = BitConverter.ToUInt16(cd, p + 32);
                c.StdOffPos = p + 42;
                c.LocalOffset = BitConverter.ToUInt32(cd, p + 42);
                c.Name = Encoding.UTF8.GetString(cd, p + 46, nl);
                c.FullLen = 46L + nl + el + cl;

                int ep = p + 46 + nl, eend = ep + el;
                while (ep + 4 <= eend)
                {
                    ushort eid = BitConverter.ToUInt16(cd, ep);
                    ushort esz = BitConverter.ToUInt16(cd, ep + 2);
                    if (eid == 1)
                    {
                        // Spec order inside ZIP64 extended information: usize, csize, offset, disk start.
                        // A field is present only when its classic counterpart is maxed out.
                        bool u = BitConverter.ToUInt32(cd, p + 24) == 0xFFFFFFFFu;
                        bool s = BitConverter.ToUInt32(cd, p + 20) == 0xFFFFFFFFu;
                        bool o = c.LocalOffset == 0xFFFFFFFFu;
                        int need = (u ? 8 : 0) + (s ? 8 : 0) + (o ? 8 : 0);
                        if (esz == need + 4) need += 4; // optional disk-number field
                        if (esz != need) throw new Exception("unexpected zip64 extra layout for '" + c.Name + "': esz=" + esz + " expected=" + need);
                        int fp = ep + 4;
                        if (u) { c.Zip64USizePos = fp; fp += 8; }
                        if (s) { c.Zip64CSizePos = fp; fp += 8; }
                        if (o) { c.Zip64OffPos = fp; fp += 8; }
                    }
                    ep += 4 + esz;
                }
                if (ep != eend) throw new Exception("extra field parse misaligned for '" + c.Name + "'");

                c.EffOffset = c.Zip64OffPos >= 0 ? (long)BitConverter.ToUInt64(cd, (int)c.Zip64OffPos) : c.LocalOffset;
                centrals.Add(c);
                p += (int)c.FullLen;
            }
            if (p != cd.Length) throw new Exception("CD parse length mismatch");
            if (centrals.Count != entryCount) throw new Exception("entry count mismatch: parsed " + centrals.Count + " vs EOCD " + entryCount);
            log.AppendLine("Parsed " + centrals.Count + " central directory records.");

            Central tgt = null;
            foreach (var c in centrals) if (c.Name == targetEntry) { if (tgt != null) throw new Exception("duplicate entry " + targetEntry); tgt = c; }
            if (tgt == null) throw new Exception("entry not found: " + targetEntry);

            // ---- inspect target's local record ----
            fs.Seek(tgt.EffOffset, SeekOrigin.Begin);
            byte[] lfh = ReadBytes(fs, 30);
            if (!(lfh[0] == 0x50 && lfh[1] == 0x4b && lfh[2] == 0x03 && lfh[3] == 0x04)) throw new Exception("bad LFH for " + targetEntry);
            ushort lflags = BitConverter.ToUInt16(lfh, 6);
            ushort lmethod = BitConverter.ToUInt16(lfh, 8);
            uint lcs = BitConverter.ToUInt32(lfh, 18);
            ushort lnl = BitConverter.ToUInt16(lfh, 26);
            ushort lel = BitConverter.ToUInt16(lfh, 28);
            byte[] lname = ReadBytes(fs, lnl);
            byte[] lextra = ReadBytes(fs, lel);
            long dataStart = tgt.EffOffset + 30 + lnl + lel;
            uint dataLen = (uint)(tgt.Zip64CSizePos >= 0 ? BitConverter.ToUInt64(cd, (int)tgt.Zip64CSizePos) : BitConverter.ToUInt32(cd, (int)tgt.CSizePos));
            uint oldUSize = (uint)(tgt.Zip64USizePos >= 0 ? BitConverter.ToUInt64(cd, (int)tgt.Zip64USizePos) : BitConverter.ToUInt32(cd, (int)tgt.USizePos));
            uint oldCrc = BitConverter.ToUInt32(cd, (int)tgt.CrcPos);
            log.AppendLine(targetEntry + ": method=" + lmethod + " flags=0x" + lflags.ToString("X4") + " csize=" + dataLen + " usize=" + oldUSize + " @offset " + tgt.EffOffset);
            if (lmethod != 0 && lcs != 0 && lcs != dataLen) throw new Exception("LFH/CD compressed size mismatch");

            fs.Seek(dataStart, SeekOrigin.Begin);
            byte[] oldComp = ReadBytes(fs, (int)dataLen);
            byte[] oldData = Inflate(lmethod, oldComp);
            if (oldData.Length != oldUSize) throw new Exception("old data size mismatch");
            if (Crc32(oldData) != oldCrc) throw new Exception("old data CRC mismatch");

            bool hasDesc = (lflags & 0x8) != 0;
            int descLen = 0; bool descSig = false;
            if (hasDesc)
            {
                fs.Seek(dataStart + dataLen, SeekOrigin.Begin);
                uint v = BitConverter.ToUInt32(ReadBytes(fs, 4), 0);
                if (v == 0x08074b50u) { descSig = true; descLen = 16; }
                else descLen = 12;
            }
            long oldRecEnd = dataStart + dataLen + descLen;

            // ---- build replacement data ----
            byte[] newComp;
            if (lmethod == 8)
            {
                using (var ms = new MemoryStream())
                {
                    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                        ds.Write(newData, 0, newData.Length);
                    newComp = ms.ToArray();
                }
            }
            else if (lmethod == 0) newComp = newData;
            else throw new Exception("unsupported method " + lmethod);
            uint newCrc = Crc32(newData);
            log.AppendLine("new: csize=" + newComp.Length + " usize=" + newData.Length + " crc=0x" + newCrc.ToString("X8"));

            // assemble new local record
            byte[] newLfh = new byte[30];
            Array.Copy(lfh, newLfh, 30);
            W32(newLfh, 14, newCrc);
            bool lfhHasSizes = !(hasDesc && lcs == 0);
            if (lfhHasSizes) { W32(newLfh, 18, (uint)newComp.Length); W32(newLfh, 22, (uint)newData.Length); }
            long newRecLen = 30 + lnl + lel + newComp.Length + descLen;
            long delta = newRecLen - (oldRecEnd - tgt.EffOffset);
            log.AppendLine("local record: old=" + (oldRecEnd - tgt.EffOffset) + " new=" + newRecLen + " delta=" + delta);

            // ---- patch central directory in memory ----
            byte[] newCd = (byte[])cd.Clone();
            foreach (var c in centrals)
            {
                if (ReferenceEquals(c, tgt))
                {
                    W32(newCd, c.CrcPos, newCrc);
                    if (c.Zip64CSizePos >= 0) W64(newCd, c.Zip64CSizePos, (ulong)newComp.Length); else W32(newCd, c.CSizePos, (uint)newComp.Length);
                    if (c.Zip64USizePos >= 0) W64(newCd, c.Zip64USizePos, (ulong)newData.Length); else W32(newCd, c.USizePos, (uint)newData.Length);
                    continue;
                }
                if (c.EffOffset > tgt.EffOffset)
                {
                    long newOff = c.EffOffset + delta;
                    if (c.Zip64OffPos >= 0) W64(newCd, c.Zip64OffPos, (ulong)newOff);
                    else
                    {
                        if (newOff > 0xFFFFFFFFL) throw new Exception("entry '" + c.Name + "' would cross the 4 GiB zip64 offset boundary; cannot patch safely");
                        W32(newCd, c.StdOffPos, (uint)newOff);
                    }
                }
            }

            // ---- write output ----
            long tailStart = cdOffset + cdSize;
            fs.Seek(tailStart, SeekOrigin.Begin);
            byte[] tailBuf = ReadBytes(fs, (int)(fileLen - tailStart));
            if (zip64)
            {
                long pos = zip64Off - tailStart;
                if (pos < 0 || pos + 56 > tailBuf.Length) throw new Exception("zip64 EOCD not in tail buffer");
                W64(tailBuf, pos + 48, (ulong)(cdOffset + delta));
                // cdSize unchanged (all CD record lengths are identical)

                // the ZIP64 locator (20 bytes right before the classic EOCD) points at the
                // ZIP64 EOCD record itself, which sits right after the central directory and
                // therefore also shifts by delta
                long locPos = (eocdOff - 20) - tailStart;
                if (locPos < 0 || locPos + 20 > tailBuf.Length) throw new Exception("zip64 locator not in tail buffer");
                if (!(tailBuf[locPos] == 0x50 && tailBuf[locPos + 1] == 0x4b && tailBuf[locPos + 2] == 0x06 && tailBuf[locPos + 3] == 0x07))
                    throw new Exception("zip64 locator signature missing");
                ulong z64Ptr = BitConverter.ToUInt64(tailBuf, (int)(locPos + 8));
                W64(tailBuf, locPos + 8, z64Ptr + (ulong)delta);
                log.AppendLine("zip64 locator: eocd offset " + z64Ptr + " -> " + (z64Ptr + (ulong)delta));
            }
            {
                long pos = eocdOff - tailStart;
                if (pos < 0 || pos + 22 > tailBuf.Length) throw new Exception("EOCD not in tail buffer");
                uint legacy = BitConverter.ToUInt32(tailBuf, (int)(pos + 16));
                if (legacy != 0xFFFFFFFFu) W32(tailBuf, pos + 16, (uint)(cdOffset + delta));
            }

            byte[] buf = new byte[1 << 20];
            using (var outFs = File.Create(dst))
            {
                CopyRange(fs, outFs, 0, tgt.EffOffset, buf);
                outFs.Write(newLfh, 0, 30);
                outFs.Write(lname, 0, lname.Length);
                if (lel > 0) outFs.Write(lextra, 0, lextra.Length);
                outFs.Write(newComp, 0, newComp.Length);
                if (hasDesc)
                {
                    byte[] desc = new byte[descLen];
                    int o = 0;
                    if (descSig) { W32(desc, 0, 0x08074b50u); o = 4; }
                    W32(desc, o, newCrc);
                    W32(desc, o + 4, (uint)newComp.Length);
                    W32(desc, o + 8, (uint)newData.Length);
                    outFs.Write(desc, 0, desc.Length);
                }
                CopyRange(fs, outFs, oldRecEnd, cdOffset, buf);
                outFs.Write(newCd, 0, newCd.Length);
                outFs.Write(tailBuf, 0, tailBuf.Length);
                outFs.Flush(true);
            }
            log.AppendLine("Wrote " + dst + " (" + new FileInfo(dst).Length + " bytes)");
        }
        return log.ToString();
    }
}
