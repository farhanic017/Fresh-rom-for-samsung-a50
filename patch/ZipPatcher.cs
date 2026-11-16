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

    /// <summary>
    /// Adds new entries to an existing ZIP archive (incl. ZIP64), keeping every other
    /// byte verbatim.  Each entry is stored (method=0) and its CRC/CD fields are
    /// freshly calculated.  After running this the new zip contains all original
    /// entries plus the supplied ones, in the central directory after the existing
    /// entries.
    /// </summary>
    /// <param name="src">source zip path</param>
    /// <param name="dst">output zip path (created)</param>
    /// <param name="entriesToAdd">list of (entryName, dataBytes) to append</param>
    public static string PatchAddEntries(string src, string dst, List<(string entryName, byte[] data)> entriesToAdd)
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
                        bool u = BitConverter.ToUInt32(cd, p + 24) == 0xFFFFFFFFu;
                        bool s = BitConverter.ToUInt32(cd, p + 20) == 0xFFFFFFFFu;
                        bool o = c.LocalOffset == 0xFFFFFFFFu;
                        int need = (u ? 8 : 0) + (s ? 8 : 0) + (o ? 8 : 0);
                        if (esz == need + 4) need += 4;
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
            foreach (var c in centrals) if (c.Name == "package.prop") { tgt = c; break; }
            if (tgt == null) { log.AppendLine("NOTE: package.prop not found; no offset adjustments needed."); }

            // ---- compute CRC and size for each new entry ----
            // We'll create entries as stored (method=0). Compute CRC32 and write data as-is.
            var newEntries = new List<(string name, uint crc, uint csize, uint usize, byte[] data)>();
            foreach (var (ename, edata) in entriesToAdd)
            {
                uint crc = Crc32(edata);
                newEntries.Add((ename, crc, (uint)edata.Length, (uint)edata.Length, edata));
                log.AppendLine("new entry: " + ename + " crc=0x" + crc.ToString("X8") + " size=" + edata.Length);
            }

            // ---- build new central directory records for added entries ----
            // Each central-dir entry: 46 + nameLen + extraFieldLen + dataLen (without zip64 extra for stored entries, may include zip64 if needed)
            // Since these are small and method=0, we store them with no zip64 extra (fields all 0xFFFFFFFF) per Fresh pattern.
            // Build local + central records concatenated at end of archive.
            // We'll allocate space after the existing tail (EOCD area).
            // Generate local-header records and write them into a temp buffer; then generate central-dir records.

            // For simplicity, we append entries after existing CD, but before EOCD.
            // New total CD size = original cdSize + sum over new entries' central-dir sizes.
            // Each new CD entry size = 46 + name.Length (nl) + extraLen (0 for stored) + 0 for extra.
            // Local header size = 30 + nameLen + extraLen + dataLen + descLen (12 if desc present).
            // Since we don't use descriptor (no extra sizes beyond 0xFFFFFFFF), descLen = 12 if csize/usize could exceed 0xFFFFFFFF but they won't for our sizes.

            int newCdEntryTotal = 0;
            foreach (var (ename, _, _, _, edata) in newEntries)
            {
                ushort nl = (ushort)ename.Length;
                int localHeaderSize = 30 + nl + edata.Length; // no extra, no descriptor
                int centralHeaderSize = 46 + nl; // no extra field for stored entry
                newCdEntryTotal += localHeaderSize + centralHeaderSize;
            }
            long newCdSize = cdSize + newCdEntryTotal;
            long newEntryCount = entryCount + newEntries.Count;

            // ---- build complete output ----
            // We'll copy source bytes up to cdOffset (start of CD), then write new CD entries
            // (including original CD entries preserved via copy), then write new entries' local+central,
            // then write the original EOCD tail (unchanged except entryCount/cdSize if plain ZIP; zip64 will be adjusted).

            // To keep it simple, we preserve the original CD entries by copying them verbatim from the source,
            // then append the new entries' records right after the original CD (i.e., before original EOCD),
            // and finally adjust the EOCD fields (cdSize and entryCount) accordingly.

            // locate original CD entries data (already in cd variable). We'll copy original CD entries.
            // Actually cd variable already contains original CD bytes. We'll append new entries' local + central after it.

            // But the output writing approach: write bytes up to cdOffset (start of CD), then write original CD (cd bytes),
            // then write new entries' local records + central records, then write original EOCD tail (with updated entryCount/cdSize).

            // compute delta for any zip64 entries referencing cd offset: zip64 cdOffset stored at z64Off+48.
            long zip64CdOffsetDelta = newCdSize - cdSize; // difference; if zip64, we must update the zip64 EOCD cdSize and cdOffset.

            // We'll need to rebuild tail: original EOCD bytes (22 bytes) + optionally zip64 structures.
            // Let's capture original tail after cdOffset+cdSize: that's the EOCD + maybe zip64.
            long tailStart = cdOffset + cdSize;
            fs.Seek(tailStart, SeekOrigin.Begin);
            byte[] origTail = ReadBytes(fs, (int)(fileLen - tailStart));
            // origTail should contain EOCD (22 bytes) starting with 0x06054b50, then optionally zip64 EOCD (maybe 56 bytes after) and zip64 locator (20 bytes before classic EOCD).
            // We need to update EOCD entryCount and cdSize fields.

            // Extract original EOCD
            if (!(origTail[0] == 0x50 && origTail[1] == 0x4b && origTail[2] == 0x05 && origTail[3] == 0x06)) throw new Exception("EOCD signature missing in tail");
            byte[] origEocd = origTail; // first 22 bytes are classic EOCD
            // For ZIP64, there may be additional structures after EOCD; but we'll assume our added entries keep it simple.

            // Build new EOCD: copy origEocd, update entryCount (uint at offset 10) and cdSize (uint at offset 12) or zip64 equivalents.
            // The EOCD format: bytes 0-5: signature, 6-7: disk number, 8-9: entry disk start, 10-11: entry count this disk, 12-13: total entry count, 14-15: cd size, 16-17: cd offset, 18-19: comment length, 20-21: comment.
            // For ZIP64, the classic EOCD fields may be 0xFFFFFFFF and the zip64 EOCD records hold the real values.

            // Since our archive is ZIP64 (cdSize > 0xFFFFFFFF), the classic EOCD fields at offsets 12-13 and 14-15 will be 0xFFFFFFFF and the actual values are in the ZIP64 EOCD record (which follows the classic EOCD, preceded by zip64 locator). We'll need to update the zip64 EOCD record's fields.

            // Let's find the zip64 EOCD and locator within origTail or after origTail if present.
            // For safety, let's just assume we update the zip64 EOCD record cdSize and cdOffset fields (at offsets 40 and 48 within the 56-byte record) if zip64 = true.

            // Simplify: if zip64 true, we will update zip64 EOCD record's cdSize at z64Off+40 and cdOffset at z64Off+48.
            // But the zip64 EOCD record sits at some offset after the classic EOCD. To find it:
            // After the classic EOCD (22 bytes), there may be a zip64 EOCD (56 bytes) if any zip64 fields were used. Since our original archive is ZIP64, there is likely a zip64 EOCD present.

            // To avoid overly complex navigation, we can take a pragmatic approach: 
            // After writing the new archive, we trust that the ZipPatcher existing code can handle reading it back. 
            // But we must adjust the zip64 structures properly.

            // Given time, let's use a known workaround: use the existing ZipPatcher.Patch method for the updater-script patch (replace one entry), and for the added entries, instead of using this new PatchAddEntries method, we will append the new entries by directly manipulating the ZIP file via .NET's ZipFile.CreateFromDirectory? Not possible.

            // Let's step back: maybe it's easier to embed the new files inside the existing repository folder by editing the updater-script to copy from a temporary location, and add the repo entries differently... Actually, the new files (Debloat.repo, GoogleMessages.apk) must physically exist in the ZIP so that `package_extract_file` can locate them. The repository folder inside the ZIP already contains many repo files; we just need to add two more files. We could add them at the end of the ZIP (as new entries). That's what PatchAddEntries aims to do.

            // Given the complexity, let's use a different strategy: We'll modify the updater-script (already done) to also handle the injection via `package_extract_file`. We need the APK to be inside the ZIP. To add the APK to the ZIP without complex patching, we can use the fact that Fresh's updater-script `package_extract_file` expects the APK to be at a known path in the zip. We could embed it by adding it as a new entry, but perhaps there's a simpler way: we can create a small "wrapper" zip that contains the original plus our files, using 7-Zip or similar, but we don't have 7-Zip. However, we can create the new entries manually.

            // Actually, there is a much simpler approach: The existing repo folder `META-INF/io/github/freshroms/repository/` in the ZIP already has entries for each addon (e.g., `google/Micro.repo`, `google/Mac.repo`?). We need to add `fresh/Debloat.repo`. Maybe we can just edit one of the existing repo files and embed our additions within an existing repo? No, Fresh reads the Debloat.repo from `META-INF/io/github/freshroms/repository/fresh/`.

            // Given extreme complexity, let's revisit the requirements: The main goal is to remove system apps and add Google Messages. The updater-script already has the removal block and Google injection block, referencing /omr paths. The repo file (Debloat.repo) triggers whiteouts during install, but perhaps we don't even strictly need the .repo file if the updater-script does all removals via `run_program(addon_remove)`. Wait, Fresh's `addon_remove` consumes .repo files to know which paths to whiteout; without the repo file, the addon_remove may have no paths to remove. However, the updater-script currently has `run_program("/tmp/tools/fresh-tk", "addon_remove", "fresh", "Debloat")`, which reads from `/tmp/repository/fresh/Debloat.repo`. The repo file we added (`C:\Users\FARHAN\AppData\Local\Temp\opencode\frsh\META-INF\io\github\freshroms\repository\fresh\Debloat.repo`) will be extracted to `/tmp/repository/` via `package_extract_dir("META-INF/io/github/freshroms/repository", "/tmp/repository")` (line 130's preceding code). So that works.

            // The critical missing piece is having the APK file inside the ZIP at `/omr/my_bigball/product/app/GoogleMessages/GoogleMessages.apk` — this is extracted by `package_extract_file("GoogleMessages.apk", "/omr/my_bigball/product/app/GoogleMessages/GoogleMessages.apk")`. For that path to resolve, the zip must contain the entry `META-INF/io/github/freshroms/repository/fresh/GoogleMessages.apk`? Actually `package_extract_file` looks for the file within the ZIP by name (relative path). The path we give is just `GoogleMessages.apk` (just the filename). In Fresh's edify, `package_extract_file("GoogleMessages.apk", "/omr/my_bigball/product/app/GoogleMessages/GoogleMessages.apk")` extracts the entry named `GoogleMessages.apk` from the ZIP to that target path. It finds the entry by exact name (just the filename) within the zip; if multiple entries have same name, ambiguous. So we just need a zip entry named exactly `GoogleMessages.apk` (no directory prefix). That's a single file entry. Similarly, the repo file `Debloat.repo` must be at path `META-INF/io/github/freshroms/repository/fresh/Debloat.repo`. That's how the extraction works: `package_extract_dir` extracts the entire `META-INF/io/github/freshroms/repository` directory tree. So after extraction, `/tmp/repository/io/github/freshroms/repository/fresh/Debloat.repo` exists. Wait, `package_extract_dir("META-INF/io/github/freshroms/repository", "/tmp/repository")` extracts the directory `META-INF/io/github/freshroms/repository` and its contents under `/tmp/repository/`. So after that, the repo directory becomes `/tmp/repository/io/github/freshroms/repository/...`. But earlier the repo path consumed is `/tmp/repository/$REPO/$ADDON.repo` where REPO is likely `fresh` and ADDON `Debloat`. The .repo format consumed reads `/tmp/repository/$REPO/$ADDON.repo` (i.e., `/tmp/repository/fresh/Debloat.repo`). However, after `package_extract_dir`, the repo file path would be `/tmp/repository/io/github/freshroms/repository/fresh/Debloat.repo`. That conflicts: the awk command expects `/tmp/repository/fresh/Debloat.repo`. There might be an extra mapping: the repo awk line strips `io/github/freshroms/repository/` prefix maybe? Wait earlier: "consumed by `awk '!/^#/ {print $0}' /tmp/repository/$REPO/$ADDON.repo`." So the repo file path is directly `/tmp/repository/$REPO/$ADDON.repo`. But `package_extract_dir` extracts `META-INF/io/github/freshroms/repository` to `/tmp/repository`. That would place files under `/tmp/repository/io/github/freshroms/repository/...`. That's a mismatch! Let's read earlier notes: "`.repo` format: `#` comments + root-relative paths; consumed by `awk '!/^#/ {print $0}' /tmp/repository/$REPO/$ADDON.repo`." The paths in .repo are root-relative, starting maybe from `system/` etc. In the google Full.repo we read earlier: lines like `system/priv-app/Velvet`. So those are paths relative to system partition. So the repo file contains path `system/priv-app/Velvet`, not full `/tmp/repository/fresh/Debloat.repo`. Wait: the consumed awk command prints lines from `/tmp/repository/$REPO/$ADDON.repo`. So the repo file lives at that path. How does `package_extract_dir` map?

Let me reread: The earlier analysis said: "`.repo` format: `#` comments + root-relative paths; consumed by `awk '!/^#/ {print $0}' /tmp/repository/$REPO/$ADDON.repo`." And "Google addon tiers: Full(1)=keep all; Micro(2) removes google Full; Lite(3) removes Full+Micro — Debloat must run regardless of tier."

Also "Fresh's own repos only whiteout app dirs." The .repo file entries are root-relative paths that get used as whiteouts in the overlay fstab. Actually, looking at the fstab: `overlay /system/{app,priv-app} ... lowerdir=/omr/my_bigball/<path>/:/<path>`. So the repo entries are paths under system/. The whiteouts hide those paths. So the .repo file entries are like `system/app/Chrome`, and those get whiteouted under /omr.

But the repo file is extracted from ZIP at `META-INF/io/github/freshroms/repository` to `/tmp/repository`. The paths inside the .repo file are like `system/priv-app/SamsungMessages`. Then the awk command prints each line, and these are used as whiteout paths under /omr/my_bigball... So the .repo file's paths are relative to the system partition root, not including `system/` prefix? Actually the .repo files we read earlier contain entries like `system/priv-app/Velvet` (full path starting with system/). The awk command prints lines as-is. Then those lines become whiteout lowerdir paths under /omr/my_bigball/. So for our Debloat.repo, entries should be root-relative paths starting with `system/` etc. Our Debloat.repo currently has entries like `product/app/Chrome` without `system/` prefix. Need to check: In the earlier analysis: "Exact removal paths: Chrome = product/app/Chrome64/ (Chrome64.apk + oat/arm64 odex/vdex); Samsung Messages = system/priv-app/SamsungMessages/ (apk, prof, art, odex, vdex)." So the repo entries probably should start with `system/` or `product/` as appropriate. Our current repo entries: `product/app/Chrome`, `product/app/Chrome64`, `system/priv-app/SamsungMessages`, `system/app/Radio`, etc. That looks consistent with what's expected: some start with `system/`, some with `product/`. That matches the Fresh pattern. So the repo entries are correct as they are.

Now, back to the main problem: adding the entries to the ZIP.

Given the complexity of extending ZipPatcher, maybe there's a simpler way: Since the `frsh` working copy already has the modified updater-script, and we have the new files (Debloat.repo and GoogleMessages.apk) placed in the correct directories inside `frsh`, we can rebuild the entire ZIP from the `frsh` directory using a zip utility. But we don't have 7z or similar. However, we can create a new ZIP by concatenating: original ZIP bytes up to some point, then our new entries, etc. This is essentially what ZipPatcher does.

Given time constraints and the fact that the environment lacks easy .NET Zip APIs, maybe the best path forward is to manually apply modifications using the existing ZipPatcher for the updater-script patch and forgo adding the two new entries (Debloat.repo + GoogleMessages.apk) physically into the ZIP, instead embedding them differently. But the requirements explicitly ask for Debloat.repo and the APK injection.

We might be able to use a simple approach: use the `Patch` method to replace the updater-script entry with our modified version (which already includes the Debloat+Google block). Then for the repo file and APK, we can embed them as new entries by modifying the ZIP's CD only, using a script that directly edits bytes. Given the limited time, let me try a different approach: manually create the new ZIP from scratch using the `frsh` directory as source and `System.IO.Compression.ZipFile` in .NET (but we saw that's not available). Instead, we can use the `zip` command if available (maybe not). Let's check.

Check if 7z or zip available:</think>
<tool_call>
<function=shell>
<parameter=command>
which zip 7z unzip 2>/dev/null || echo "no zip tools"
