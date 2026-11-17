using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace X2HD2D.Core
{
    public sealed class CaptureFormatException : IOException
    {
        public CaptureFormatException(string reason) : base(reason) { }
    }

    public sealed class MemoryImage
    {
        private readonly byte[] bytes;
        internal MemoryImage(byte[] data) { bytes = data; }
        public int Length => bytes.Length;
        public byte Byte(int index) => bytes[index];
        public ushort Word(int index) => (ushort)(bytes[index * 2] | bytes[index * 2 + 1] << 8);
        public byte[] CopyBytes() => (byte[])bytes.Clone();
        internal bool Same(MemoryImage other) => bytes.SequenceEqual(other.bytes);
    }

    public sealed class LineState
    {
        private readonly uint[] registers;
        public int Line { get; }
        public MemoryImage Vram { get; }
        public MemoryImage Cgram { get; }
        public MemoryImage Oam { get; }
        internal LineState(int line, uint[] values, MemoryImage vram, MemoryImage cgram, MemoryImage oam)
        { Line = line; registers = values; Vram = vram; Cgram = cgram; Oam = oam; }
        public uint Raw(int field) => registers[field];
        public int Get(string name)
        {
            int index = -1;
            for (int i = 0; i < WireFields.Names.Count; i++) if (WireFields.Names[i] == name) { index = i; break; }
            if (index < 0) throw new ArgumentException("Unknown wire field: " + name);
            return unchecked((int)registers[index]);
        }
    }

    public sealed class ScanoutCapture
    {
        private readonly byte[] reference;
        public IReadOnlyList<LineState> Lines { get; }
        public string Session { get; }
        public string Producer { get; }
        public string Engine { get; }
        public string ContainerHash { get; }
        public ulong Epoch { get; }
        public ulong Sequence { get; }
        public ulong Draw { get; }
        public ulong GuestFrameBits { get; }
        public ulong MasterCycles { get; }
        public uint RendererFlags { get; }
        public uint Source { get; }
        internal ScanoutCapture(byte[] data, LineState[] lines)
        {
            Lines = Array.AsReadOnly(lines);
            Session = ScanoutReader.Hex(data, 40, 16);
            Producer = Encoding.ASCII.GetString(data, 96, 40);
            Engine = Encoding.ASCII.GetString(data, 136, 40);
            ContainerHash = ScanoutReader.Hex(data, data.Length - 32, 32);
            Epoch = ScanoutReader.U64(data, 56); Sequence = ScanoutReader.U64(data, 64);
            Draw = ScanoutReader.U64(data, 72); GuestFrameBits = ScanoutReader.U64(data, 80);
            MasterCycles = ScanoutReader.U64(data, 88); RendererFlags = ScanoutReader.U32(data, 176);
            Source = ScanoutReader.U32(data, 36);
            reference = new byte[ScanoutReader.RgbaBytes];
            Buffer.BlockCopy(data, ScanoutReader.ReferenceOffset, reference, 0, reference.Length);
        }
        public byte[] CopyReferenceRgba() => (byte[])reference.Clone();
    }

    // No Unity dependency, native interop, ROM access, external blobs or executable payloads.
    public static class ScanoutReader
    {
        public const int MaxBytes = 4194304, Width = 256, Height = 224, LineCount = 225;
        public const int HeaderBytes = 240, FieldCount = 70, LineBytes = 296;
        public const int ReferenceOffset = 66840, RgbaBytes = 229376, MemoryOffset = 296216;
        public const string EnginePin = "93fc677a2a9b688751d575ed1232343d83d3d2c0";
        internal static uint U32(byte[] b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);
        internal static ulong U64(byte[] b, int p) => U32(b, p) | (ulong)U32(b, p + 4) << 32;
        internal static string Hex(byte[] b, int p, int n) => BitConverter.ToString(b, p, n).Replace("-", "").ToLowerInvariant();
        private static void Require(bool condition, string reason)
        { if (!condition) throw new CaptureFormatException(reason); }

        public static ScanoutCapture ReadFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(stream.Length >= MemoryOffset + 32 && stream.Length <= MaxBytes, "Size outside 4 MiB runtime budget");
                byte[] data = new byte[(int)stream.Length];
                int offset = 0, count;
                while (offset < data.Length && (count = stream.Read(data, offset, data.Length - offset)) > 0) offset += count;
                Require(offset == data.Length && stream.ReadByte() == -1, "Capture changed/truncated while reading");
                return Decode(data);
            }
        }

        public static ScanoutCapture Decode(byte[] input)
        {
            Require(input != null && input.Length >= MemoryOffset + 32 && input.Length <= MaxBytes, "Size outside runtime budget");
            byte[] data = (byte[])input.Clone(); // Caller cannot mutate an accepted capture.
            Require(Encoding.ASCII.GetString(data, 0, 8) == "X2SCAN01" && U32(data, 8) == 1 && U32(data, 12) == 1, "Unknown format/version/profile");
            Require(U32(data, 16) == data.Length && U32(data, 20) == LineCount && U32(data, 24) == Width &&
                U32(data, 28) == Height && U32(data, 32) == FieldCount, "Invalid dimensions/counts/completeness");
            Require(U32(data, 36) <= 1 && data.Skip(40).Take(16).Any(b => b != 0) && U64(data, 64) != 0 && U64(data, 72) != 0, "Invalid capture identity");
            Require(data.Skip(96).Take(80).All(b => b >= '0' && b <= '9' || b >= 'a' && b <= 'f'), "Invalid revision text");
            Require(Encoding.ASCII.GetString(data, 136, 40) == EnginePin, "Unqualified PPU engine revision");
            Require((U32(data, 176) & ~9u) == 0, "Unknown renderer flags");
            using (var sha = SHA256.Create())
                Require(sha.ComputeHash(data, 0, data.Length - 32).SequenceEqual(data.Skip(data.Length - 32)), "Checksum mismatch");
            int[] sizes = { 65536, 512, 544 };
            int[] counts = new int[3];
            var memories = new List<MemoryImage>[3];
            for (int k = 0; k < 3; k++)
            {
                uint count = U32(data, 180 + k * 4);
                Require(count >= 1 && count <= LineCount, "Invalid memory version count");
                counts[k] = (int)count; memories[k] = new List<MemoryImage>(counts[k]);
            }
            int pos = MemoryOffset, end = data.Length - 32;
            while (pos < end)
            {
                Require(end - pos >= 12, "Truncated memory descriptor");
                uint kind = U32(data, pos), id = U32(data, pos + 4), size = U32(data, pos + 8); pos += 12;
                Require(kind < 3, "Unknown memory kind");
                int k = (int)kind;
                Require(id == memories[k].Count && id < counts[k] && size == sizes[k] && size <= end - pos, "Invalid memory descriptor/image");
                byte[] image = new byte[sizes[k]]; Buffer.BlockCopy(data, pos, image, 0, image.Length);
                memories[k].Add(new MemoryImage(image)); pos += image.Length;
            }
            Require(Enumerable.Range(0, 3).All(k => memories[k].Count == counts[k]), "Missing memory image");
            int[] last = new int[3];
            var lines = new LineState[LineCount];
            for (int line = 0; line < LineCount; line++)
            {
                int p = HeaderBytes + line * LineBytes;
                Require(U32(data, p) == line, "Missing/reordered scanline " + line);
                int[] ids = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    uint id = U32(data, p + 4 + k * 4);
                    Require(id < counts[k] && id >= last[k] && id <= last[k] + 1 && (line != 0 || id == 0), "Invalid memory reference at line " + line);
                    last[k] = ids[k] = (int)id;
                }
                uint[] regs = new uint[FieldCount];
                for (int f = 0; f < FieldCount; f++)
                {
                    uint value = regs[f] = U32(data, p + 16 + f * 4);
                    string type = WireFields.Types[f];
                    bool valid = type == "u8" ? value <= 255 : type == "u16" ? value <= 65535 : type == "bit" ? value <= 1 :
                        type == "s16" ? value <= 32767 || value >= 0xffff8000u : true;
                    Require(valid, "Invalid register " + WireFields.Names[f] + " at line " + line);
                }
                var state = new LineState(line, regs, memories[0][ids[0]], memories[1][ids[1]], memories[2][ids[2]]);
                Require((state.Get("bgmode") & 7) == 1, "Unsupported display mode at line " + line);
                foreach (string field in new[] { "mosaic", "setini", "frameOverscan", "frameInterlace" })
                    Require(state.Get(field) == 0, "Unsupported " + field + " at line " + line);
                lines[line] = state;
            }
            Require(Enumerable.Range(0, 3).All(k => last[k] + 1 == counts[k]), "Unused memory version");
            for (int i = ReferenceOffset + 3; i < MemoryOffset; i += 4) Require(data[i] == 255, "Noncanonical reference alpha");
            return new ScanoutCapture(data, lines);
        }
    }
}
