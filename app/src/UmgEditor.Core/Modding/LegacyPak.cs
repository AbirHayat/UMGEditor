using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace UmgEditor.Core.Modding;

/// <summary>A file in a legacy pak: path relative to the mount point, its bytes, and (only when read from a pak or for the
/// stock anchor) the single Zlib block it was stored as.</summary>
public sealed record PakFile(string Path, byte[] Data, byte[]? ZlibBlock = null);

/// <summary>What a legacy pak holds. CompressionNames are FPakInfo's five method names ("" = unused).</summary>
public sealed record PakContents(string MountPoint, ulong PathHashSeed, IReadOnlyList<string> CompressionNames, IReadOnlyList<PakFile> Files);

/// <summary>
/// Legacy .pak v11 (UE 4.26, PakFile_Version_Fnv64BugFix) in the shape every stock mobile pak has (checked on
/// pc0010/pc0100/pc7000/pakchunk0): entries not encrypted, the index AES-256-ECB encrypted with the main key (GUID 0).
/// Layout: per file a 53-byte FPakEntry header (Offset 0, Size, UncompressedSize, method, SHA-1 of the stored bytes,
/// [blocks], flags, block size) then the bytes; the primary index (mount, count, seed, PathHashIndex and
/// FullDirectoryIndex offset/size/SHA-1, encoded entries, no unencoded ones); the PathHashIndex (FNV-64 of the
/// lower-cased UTF-16 path + seed → location, then the pruned directory index, here = the full one, as pc0010 has);
/// the FullDirectoryIndex (dir → file → location); FPakInfo (221 B). Each index block is padded to 16 by repeating its
/// own bytes from the start, SHA-1'd, then encrypted; the stored sizes are the padded ones.
/// Writes stored files, all in the mount point's folder ("/"); a single already-compressed Zlib block is accepted so
/// stock pc0010 can be rebuilt byte for byte (the anchor). Read parses the same shape and checks every hash.
/// </summary>
public static class LegacyPak
{
    public const int Version = 11;
    public const uint Magic = 0x5A6F12E1;
    public const int InfoSize = 16 + 1 + 4 + 4 + 8 + 8 + 20 + 5 * 32;
    private const ulong FnvOffset = 0xcbf29ce484222325, FnvPrime = 0x00000100000001b3;

    /// <summary>FPakFile::HashPath for v11: FNV-64 (offset basis + seed) over the lower-cased path as UTF-16LE.</summary>
    public static ulong HashPath(string relativePath, ulong seed)
    {
        var h = FnvOffset + seed;
        foreach (var b in Encoding.Unicode.GetBytes(relativePath.ToLowerInvariant())) { h ^= b; h *= FnvPrime; }
        return h;
    }

    /// <summary>The seed for a new pak: CRC-32 of the lower-cased file name as UTF-32 (FCrc::StrCrc32's input); any value works, the reader uses the stored one.</summary>
    public static ulong SeedFor(string pakFileName)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF32.GetBytes(pakFileName.ToLowerInvariant()))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static byte[] Write(PakContents contents, byte[] aesKey)
    {
        if (!contents.MountPoint.EndsWith('/')) throw new ArgumentException($"mount point {contents.MountPoint} must end with /");
        if (contents.CompressionNames.Count != 5) throw new ArgumentException("five compression names expected");
        var zlib = contents.CompressionNames.ToList().IndexOf("Zlib") + 1;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in contents.Files)
        {
            if (f.Path.Length == 0 || f.Path.Contains('/') || f.Path.Contains('\\')) throw new ArgumentException($"{f.Path}: only files directly in the mount point's folder are supported");
            if (!names.Add(f.Path)) throw new ArgumentException($"{f.Path} is in the pak twice");
            if (f.ZlibBlock != null && zlib == 0) throw new ArgumentException($"{f.Path} is Zlib-compressed but Zlib isn't among the compression names");
            if (f.ZlibBlock != null && f.Data.Length >> 11 > 0x3f) throw new ArgumentException($"{f.Path}: a single Zlib block over 0x3f << 11 bytes can't be encoded");
        }

        // data: header + stored bytes per file
        var data = new MemoryStream();
        var encoded = new MemoryStream();
        var locations = new List<int>();
        foreach (var f in contents.Files)
        {
            var stored = f.ZlibBlock ?? f.Data;
            var offset = data.Position;
            var compressed = f.ZlibBlock != null;
            var headerSize = 8 + 8 + 8 + 4 + 20 + (compressed ? 4 + 16 : 0) + 1 + 4;
            var w = new BinaryWriter(data);
            w.Write(0L);
            w.Write((long)stored.Length);
            w.Write((long)f.Data.Length);
            w.Write(compressed ? zlib : 0);
            w.Write(SHA1.HashData(stored));
            if (compressed) { w.Write(1); w.Write((long)headerSize); w.Write((long)(headerSize + stored.Length)); }
            w.Write((byte)0);
            w.Write(compressed ? f.Data.Length : 0);
            w.Write(stored);

            locations.Add((int)encoded.Position);
            var e = new BinaryWriter(encoded);
            uint bits = (offset <= uint.MaxValue ? 1u << 31 : 0) | (f.Data.Length <= uint.MaxValue ? 1u << 30 : 0) | (stored.Length <= uint.MaxValue ? 1u << 29 : 0);
            if (compressed) bits |= (uint)zlib << 23 | 1u << 6 | (uint)(f.Data.Length >> 11);
            e.Write(bits);
            if (offset <= uint.MaxValue) e.Write((uint)offset); else e.Write(offset);
            e.Write((uint)f.Data.Length);
            if (compressed) e.Write((uint)stored.Length);
        }

        // the directory index: everything in "/" (the mount point's folder)
        byte[] DirectoryIndex()
        {
            var m = new MemoryStream();
            var w = new BinaryWriter(m);
            if (contents.Files.Count == 0) { w.Write(0); return m.ToArray(); }
            w.Write(1);
            WriteFString(w, "/");
            w.Write(contents.Files.Count);
            for (var i = 0; i < contents.Files.Count; i++) { WriteFString(w, contents.Files[i].Path); w.Write(locations[i]); }
            return m.ToArray();
        }
        var phiPlain = new MemoryStream();
        var pw = new BinaryWriter(phiPlain);
        pw.Write(contents.Files.Count);
        for (var i = 0; i < contents.Files.Count; i++) { pw.Write(HashPath(contents.Files[i].Path, contents.PathHashSeed)); pw.Write(locations[i]); }
        pw.Write(DirectoryIndex());
        var (phi, phiHash) = Seal(phiPlain.ToArray(), aesKey);
        var (fdi, fdiHash) = Seal(DirectoryIndex(), aesKey);

        byte[] Primary(long phiOffset, long fdiOffset)
        {
            var m = new MemoryStream();
            var w = new BinaryWriter(m);
            WriteFString(w, contents.MountPoint);
            w.Write(contents.Files.Count);
            w.Write(contents.PathHashSeed);
            w.Write(1); w.Write(phiOffset); w.Write((long)phi.Length); w.Write(phiHash);
            w.Write(1); w.Write(fdiOffset); w.Write((long)fdi.Length); w.Write(fdiHash);
            w.Write((int)encoded.Length); w.Write(encoded.ToArray());
            w.Write(0);
            return m.ToArray();
        }
        var indexOffset = data.Length;
        var primaryLength = Align16(Primary(0, 0).Length);
        var (primary, primaryHash) = Seal(Primary(indexOffset + primaryLength, indexOffset + primaryLength + phi.Length), aesKey);

        var o = new MemoryStream();
        var ow = new BinaryWriter(o);
        ow.Write(data.ToArray());
        ow.Write(primary);
        ow.Write(phi);
        ow.Write(fdi);
        ow.Write(new byte[16]);
        ow.Write((byte)1);
        ow.Write(Magic);
        ow.Write(Version);
        ow.Write(indexOffset);
        ow.Write((long)primary.Length);
        ow.Write(primaryHash);
        foreach (var n in contents.CompressionNames)
        {
            var b = new byte[32];
            Encoding.ASCII.GetBytes(n).CopyTo(b, 0);
            ow.Write(b);
        }
        return o.ToArray();
    }

    /// <summary>Parses a pak of this shape and checks it: every index SHA-1, each file's header against its encoded
    /// entry, the stored bytes' SHA-1, the PathHashIndex against the directory index. Null contents = refused.</summary>
    public static (PakContents? Contents, IReadOnlyList<string> Problems) Read(byte[] pak, byte[] aesKey)
    {
        var problems = new List<string>();
        try
        {
            if (pak.Length < InfoSize) return (null, ["shorter than FPakInfo"]);
            var info = new BinaryReader(new MemoryStream(pak, pak.Length - InfoSize, InfoSize));
            var guid = info.ReadBytes(16);
            var encryptedIndex = info.ReadByte();
            if (info.ReadUInt32() != Magic) return (null, ["no pak magic in FPakInfo"]);
            var version = info.ReadInt32();
            if (version != Version) return (null, [$"version {version}, only {Version} is supported"]);
            var indexOffset = info.ReadInt64();
            var indexSize = info.ReadInt64();
            var indexHash = info.ReadBytes(20);
            var compressionNames = Enumerable.Range(0, 5).Select(_ => Encoding.ASCII.GetString(info.ReadBytes(32)).TrimEnd('\0')).ToList();
            if (guid.Any(b => b != 0)) problems.Add("encryption key GUID isn't zero");
            if (encryptedIndex != 1) problems.Add("index isn't encrypted (every stock mobile pak's is)");

            byte[] Block(long offset, long size, byte[] hash, string what)
            {
                if (offset < 0 || size < 0 || offset + size > pak.Length - InfoSize) throw new InvalidDataException($"{what} at {offset}+{size} is outside the pak");
                var raw = pak.AsSpan((int)offset, (int)size).ToArray();
                var plain = encryptedIndex == 1 ? Decrypt(raw, aesKey) : raw;
                if (!SHA1.HashData(plain).AsSpan().SequenceEqual(hash)) problems.Add($"{what} SHA-1 doesn't match");
                return plain;
            }
            var p = new BinaryReader(new MemoryStream(Block(indexOffset, indexSize, indexHash, "primary index")));
            var mount = ReadFString(p);
            var count = p.ReadInt32();
            var seed = p.ReadUInt64();
            if (p.ReadInt32() != 1) return (null, ["no PathHashIndex"]);
            var (phiOffset, phiSize, phiHash) = (p.ReadInt64(), p.ReadInt64(), p.ReadBytes(20));
            if (p.ReadInt32() != 1) return (null, ["no FullDirectoryIndex"]);
            var (fdiOffset, fdiSize, fdiHash) = (p.ReadInt64(), p.ReadInt64(), p.ReadBytes(20));
            var encoded = p.ReadBytes(p.ReadInt32());
            if (p.ReadInt32() != 0) return (null, ["unencoded entries aren't supported"]);
            if (indexOffset + indexSize != phiOffset || phiOffset + phiSize != fdiOffset || fdiOffset + fdiSize != pak.Length - InfoSize)
                problems.Add("index blocks aren't contiguous (data, primary, PathHashIndex, FullDirectoryIndex, FPakInfo)");

            var fdiReader = new BinaryReader(new MemoryStream(Block(fdiOffset, fdiSize, fdiHash, "FullDirectoryIndex")));
            var files = new List<(string Dir, string Name, int Location)>();
            var dirs = fdiReader.ReadInt32();
            for (var d = 0; d < dirs; d++)
            {
                var dir = ReadFString(fdiReader);
                var n = fdiReader.ReadInt32();
                for (var i = 0; i < n; i++) files.Add((dir, ReadFString(fdiReader), fdiReader.ReadInt32()));
            }
            if (files.Count != count) problems.Add($"primary index counts {count} files, the directory index lists {files.Count}");

            var phiReader = new BinaryReader(new MemoryStream(Block(phiOffset, phiSize, phiHash, "PathHashIndex")));
            var hashes = new Dictionary<ulong, int>();
            var hn = phiReader.ReadInt32();
            for (var i = 0; i < hn; i++) hashes[phiReader.ReadUInt64()] = phiReader.ReadInt32();
            foreach (var (dir, name, loc) in files)
            {
                var rel = dir == "/" ? name : dir + name;
                if (!hashes.TryGetValue(HashPath(rel, seed), out var hl) || hl != loc) problems.Add($"{rel}: PathHashIndex doesn't point at its entry");
            }
            if (hn != files.Count) problems.Add($"PathHashIndex has {hn} hashes for {files.Count} files");

            var result = new List<PakFile>();
            foreach (var (dir, name, loc) in files)
            {
                var rel = dir == "/" ? name : dir + name;
                var e = new BinaryReader(new MemoryStream(encoded, loc, encoded.Length - loc));
                var bits = e.ReadUInt32();
                long offset = (bits & 1u << 31) != 0 ? e.ReadUInt32() : e.ReadInt64();
                long usize = (bits & 1u << 30) != 0 ? e.ReadUInt32() : e.ReadInt64();
                var method = (int)(bits >> 23 & 0x3f);
                long size = method == 0 ? usize : (bits & 1u << 29) != 0 ? e.ReadUInt32() : e.ReadInt64();
                var blocks = (int)(bits >> 6 & 0xffff);
                var encrypted = (bits >> 22 & 1) != 0;
                if (encrypted) { problems.Add($"{rel}: encrypted entries aren't supported"); continue; }
                if (method != 0 && (blocks != 1 || compressionNames[method - 1] != "Zlib")) { problems.Add($"{rel}: only stored or one Zlib block is supported"); continue; }

                var h = new BinaryReader(new MemoryStream(pak, (int)offset, pak.Length - (int)offset));
                var (hOffset, hSize, hUsize, hMethod, hHash) = (h.ReadInt64(), h.ReadInt64(), h.ReadInt64(), h.ReadInt32(), h.ReadBytes(20));
                long headerSize = 53;
                if (hMethod != 0)
                {
                    var n = h.ReadInt32();
                    headerSize += 4 + 16L * n;
                    for (var i = 0; i < n; i++) { var (s, end) = (h.ReadInt64(), h.ReadInt64()); if (n == 1 && (s != headerSize || end != headerSize + size)) problems.Add($"{rel}: block isn't right after the header"); }
                }
                var (hFlags, hBlockSize) = (h.ReadByte(), h.ReadUInt32());
                // block size: 0 when stored; for one Zlib block stock writes the file size, UCL's tool a multiple of 2048 above it; both encode as size >> 11
                var blockOk = method == 0 ? hBlockSize == 0 : hBlockSize >= usize && (hBlockSize >> 11) == (bits & 0x3f);
                if (hOffset != 0 || hSize != size || hUsize != usize || hMethod != method || hFlags != 0 || !blockOk)
                    problems.Add($"{rel}: header (offset {hOffset}, size {hSize}, usize {hUsize}, method {hMethod}, flags {hFlags}, block {hBlockSize}) doesn't match its entry");
                var stored = pak.AsSpan((int)(offset + headerSize), (int)size).ToArray();
                if (!SHA1.HashData(stored).AsSpan().SequenceEqual(hHash)) problems.Add($"{rel}: SHA-1 of the stored bytes doesn't match its header");
                byte[] bytes = stored;
                if (method != 0)
                {
                    using var z = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
                    var m = new MemoryStream();
                    z.CopyTo(m);
                    bytes = m.ToArray();
                    if (bytes.Length != usize) problems.Add($"{rel}: decompresses to {bytes.Length} bytes, the entry says {usize}");
                }
                result.Add(new PakFile(rel, bytes, method != 0 ? stored : null));
            }
            return (new PakContents(mount, seed, compressionNames, result), problems);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException or IOException)
        {
            return (null, [$"unreadable: {ex.Message}"]);
        }
    }

    private static int Align16(int n) => (n + 15) / 16 * 16;

    /// <summary>UnrealPak's FinalizeIndexBlock: pad to 16 by repeating the block's own bytes, SHA-1 the padded plain text, encrypt.</summary>
    private static (byte[] Bytes, byte[] Sha1) Seal(byte[] plain, byte[] key)
    {
        var padded = new byte[Align16(plain.Length)];
        for (var i = 0; i < padded.Length; i++) padded[i] = plain.Length == 0 ? (byte)0 : plain[i % plain.Length];
        var hash = SHA1.HashData(padded);
        using var aes = Aes.Create();
        aes.Key = key;
        return (aes.EncryptEcb(padded, PaddingMode.None), hash);
    }

    private static byte[] Decrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, PaddingMode.None);
    }

    private static void WriteFString(BinaryWriter w, string s)
    {
        if (s.All(c => c < 128))
        {
            w.Write(s.Length + 1);
            w.Write(Encoding.ASCII.GetBytes(s));
            w.Write((byte)0);
        }
        else
        {
            w.Write(-(s.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(s));
            w.Write((short)0);
        }
    }

    private static string ReadFString(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n == 0) return "";
        return n > 0 ? Encoding.ASCII.GetString(r.ReadBytes(n))[..^1] : Encoding.Unicode.GetString(r.ReadBytes(-2 * n))[..^1];
    }

    public static byte[] KeyBytes(string hexKey) => Convert.FromHexString(hexKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hexKey[2..] : hexKey);
}
