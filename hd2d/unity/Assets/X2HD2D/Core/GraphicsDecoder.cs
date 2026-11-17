using System;
using System.Collections.Generic;
using System.Linq;

namespace X2HD2D.Core
{
    public sealed class BackgroundPlane
    {
        public int Layer, Priority, SortOrder, MapBaseWord, TileBaseWord, MapWidth, MapHeight, TileSize, ScrollX, ScrollY, BitsPerPixel;
        public bool MainEnabled;
        public ushort[] Tilemap;
        public byte[] Rgba;
    }
    public sealed class OamPiece
    {
        public int Slot, EvaluationOrder, X, Y, Size, TileNumber, Palette, Priority, SortOrder, TileBaseWord;
        public bool FlipX, FlipY, Large, MainEnabled;
        public byte[] RawRgba, VisibleRgba;
    }
    public sealed class Reconstruction
    {
        public LineState State;
        public BackgroundPlane[] Backgrounds;
        public OamPiece[] Pieces;
        public byte[] Backdrop;
        public string[] UnsupportedReasons;
        public bool IsSupported => UnsupportedReasons.Length == 0;
        public bool ForcedBlank;
    }

    // Independent managed component decoder. Reference pixels are never an input.
    public static class GraphicsDecoder
    {
        private static readonly int[,] Sizes = { {8,16}, {8,32}, {8,64}, {16,32}, {16,64}, {32,64}, {16,32}, {16,32} };
        private static readonly string[] VisualFields = WireFields.Names.Take(21).Concat(WireFields.Names.Skip(29).Take(13)).ToArray();
        public static int PlanarPixel(MemoryImage vram, int baseWord, int tile, int bits, int x, int y)
        {
            if ((bits != 2 && bits != 4) || x < 0 || x > 7 || y < 0 || y > 7) throw new ArgumentOutOfRangeException();
            int pixel = 0;
            for (int pair = 0; pair < bits / 2; pair++)
            {
                int word = vram.Word((baseWord + tile * bits * 4 + pair * 8 + y) & 0x7fff);
                pixel |= ((word >> (7 - x)) & 1) << (pair * 2);
                pixel |= ((word >> (15 - x)) & 1) << (pair * 2 + 1);
            }
            return pixel;
        }
        public static byte[] PaletteColor(MemoryImage cgram, int index, int brightness)
        {
            int rgb = cgram.Word(index & 255);
            var result = new byte[] { 0, 0, 0, 255 };
            for (int c = 0; c < 3; c++) { int v = rgb >> (c * 5) & 31; result[c] = (byte)(((v << 3) | (v >> 2)) * brightness / 15); }
            return result;
        }
        private static void Pixel(byte[] image, int offset, byte[] color) => Buffer.BlockCopy(color, 0, image, offset, 4);
        public static int BackgroundOrder(int layer, int priority, bool bg3High)
        {
            int[,] normal = { { 5, 8 }, { 4, 7 }, { 0, 2 } };
            int[,] high = { { 4, 7 }, { 3, 6 }, { 0, 9 } };
            return (bg3High ? high : normal)[layer, priority] * 1000;
        }
        public static int ObjectOrder(int priority, bool bg3High) => (bg3High ? new[] { 1, 2, 5, 8 } : new[] { 1, 3, 6, 9 })[priority] * 1000;
        private static int MapAddress(int baseWord, int cellX, int cellY, int width, int height)
        {
            cellX &= width - 1; cellY &= height - 1;
            return (baseWord + (cellY & 31) * 32 + (cellX & 31) + (cellX >> 5) * 1024 + (cellY >> 5) * (width == 64 ? 2048 : 1024)) & 0x7fff;
        }
        public static string[] Assess(ScanoutCapture capture)
        {
            var reasons = new List<string>();
            LineState first = capture.Lines[1];
            for (int line = 1; line < 225; line++)
            {
                var s = capture.Lines[line];
                if ((s.Get("cgadsub") & 63) != 0 || (s.Get("cgwsel") & 0xf0) != 0)
                    Add(reasons, "Color math/color-window clipping is not reconstructed");
                if (s.Get("windowsel") != 0 || s.Get("screenWindowed[0]") != 0 || s.Get("screenWindowed[1]") != 0)
                    Add(reasons, "Layer windowing is not reconstructed");
                if ((s.Get("obsel") >> 5) >= 6) Add(reasons, "Rectangular OBJ size modes 6/7 are not qualified (inspection uses pinned square approximation)");
                string changed = VisualFields.FirstOrDefault(f => s.Get(f) != first.Get(f));
                if (changed != null && !reasons.Any(r => r.StartsWith("Raster registers")))
                    reasons.Add("Raster registers change at line " + line + ": " + changed);
                if ((!ReferenceEquals(s.Vram, first.Vram) && !s.Vram.Same(first.Vram) ||
                     !ReferenceEquals(s.Cgram, first.Cgram) && !s.Cgram.Same(first.Cgram) ||
                     !ReferenceEquals(s.Oam, first.Oam) && !s.Oam.Same(first.Oam)) && !reasons.Any(r => r.StartsWith("Raster memory")))
                    reasons.Add("Raster memory changes at line " + line + "; selected-state inspection is not full-frame replay");
            }
            return reasons.ToArray();
        }
        private static void Add(List<string> reasons, string reason) { if (!reasons.Contains(reason)) reasons.Add(reason); }

        public static Reconstruction Decode(ScanoutCapture capture, int selectedLine = 1)
        {
            if (selectedLine < 1 || selectedLine > 224) throw new ArgumentOutOfRangeException(nameof(selectedLine));
            LineState s = capture.Lines[selectedLine];
            int brightness = s.Get("inidisp") & 15;
            bool bg3High = (s.Get("bgmode") & 8) != 0;
            var result = new Reconstruction { State = s, UnsupportedReasons = Assess(capture), ForcedBlank = (s.Get("inidisp") & 128) != 0,
                Backdrop = PaletteColor(s.Cgram, 0, brightness), Backgrounds = new BackgroundPlane[6], Pieces = new OamPiece[128] };
            var palette = Enumerable.Range(0, 256).Select(i => PaletteColor(s.Cgram, i, brightness)).ToArray();
            for (int layer = 0; layer < 3; layer++)
            {
                int map = s.Get("bgXsc[" + layer + "]"), size = (s.Get("bgmode") & (16 << layer)) != 0 ? 16 : 8;
                int width = (map & 1) != 0 ? 64 : 32, height = (map & 2) != 0 ? 64 : 32;
                int mapBase = (map & 252) << 8, tileBase = (s.Get("bgTileAdr") >> (4 * layer) & 15) << 12;
                int scrollX = s.Get("hScroll[" + layer + "]"), scrollY = s.Get("vScroll[" + layer + "]"), bits = layer == 2 ? 2 : 4;
                var tilemap = new ushort[width * height];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) tilemap[y * width + x] = s.Vram.Word(MapAddress(mapBase, x, y, width, height));
                for (int priority = 0; priority < 2; priority++) result.Backgrounds[layer * 2 + priority] = new BackgroundPlane {
                    Layer = layer, Priority = priority, SortOrder = BackgroundOrder(layer, priority, bg3High), MapBaseWord = mapBase,
                    TileBaseWord = tileBase, MapWidth = width, MapHeight = height, TileSize = size, ScrollX = scrollX, ScrollY = scrollY,
                    BitsPerPixel = bits, MainEnabled = (s.Get("screenEnabled[0]") & (1 << layer)) != 0, Tilemap = tilemap, Rgba = new byte[ScanoutReader.RgbaBytes] };
                for (int y = 0; y < 224; y++) for (int x = 0; x < 256; x++)
                {
                    // BG samples the native line (1..224); OBJ samples line-1.
                    int sx = (x + scrollX) & 1023, sy = (y + 1 + scrollY) & 1023;
                    int entry = s.Vram.Word(MapAddress(mapBase, sx / size, sy / size, width, height));
                    int tx = sx & (size - 1), ty = sy & (size - 1);
                    if ((entry & 0x4000) != 0) tx = size - 1 - tx;
                    if ((entry & 0x8000) != 0) ty = size - 1 - ty;
                    int tile = ((entry & 1023) + (tx >> 3) + (ty >> 3) * 16) & 1023;
                    int color = PlanarPixel(s.Vram, tileBase, tile, bits, tx & 7, ty & 7);
                    if (color != 0) Pixel(result.Backgrounds[layer * 2 + ((entry >> 13) & 1)].Rgba, (y * 256 + x) * 4, palette[((entry >> 10) & 7) * (1 << bits) + color]);
                }
            }
            int firstSlot = (s.Get("oamaddh") & 128) != 0 ? (s.Get("oamaddl") & 254) / 2 : 0;
            for (int slot = 0; slot < 128; slot++)
            {
                int xy = s.Oam.Word(slot * 2), attr = s.Oam.Word(slot * 2 + 1), high = s.Oam.Byte(512 + slot / 4) >> ((slot % 4) * 2);
                int x = (xy & 255) | ((high & 1) << 8); if (x >= 256) x -= 512;
                int size = Sizes[s.Get("obsel") >> 5, (high >> 1) & 1], objBase = (s.Get("obsel") & 7) << 13;
                if ((attr & 256) != 0) objBase += ((s.Get("obsel") & 24) + 8) << 9;
                var piece = new OamPiece { Slot = slot, EvaluationOrder = (slot - firstSlot + 128) % 128, X = x, Y = xy >> 8,
                    Size = size, Large = (high & 2) != 0, TileNumber = attr & 511, Palette = (attr >> 9) & 7, Priority = (attr >> 12) & 3,
                    FlipX = (attr & 0x4000) != 0, FlipY = (attr & 0x8000) != 0, TileBaseWord = objBase & 0x7fff,
                    MainEnabled = (s.Get("screenEnabled[0]") & 16) != 0, RawRgba = new byte[size * size * 4], VisibleRgba = new byte[size * size * 4] };
                piece.SortOrder = ObjectOrder(piece.Priority, bg3High) + 127 - piece.EvaluationOrder;
                for (int py = 0; py < size; py++) for (int px = 0; px < size; px++)
                {
                    int tx = piece.FlipX ? size - 1 - px : px, ty = piece.FlipY ? size - 1 - py : py;
                    int tile = ((((attr & 255) >> 4) + (ty >> 3)) << 4) | (((attr & 15) + (tx >> 3)) & 15);
                    int color = PlanarPixel(s.Vram, objBase, tile, 4, tx & 7, ty & 7);
                    if (color != 0) Pixel(piece.RawRgba, (py * size + px) * 4, palette[128 + piece.Palette * 16 + color]);
                }
                result.Pieces[slot] = piece;
            }
            // Match pinned sprite range selection, reverse tile fetching and lower-OAM ownership.
            // Mask BEFORE background priority sorting: sorting whole sprites alone is incorrect.
            int[] owner = Enumerable.Repeat(-1, 256 * 224).ToArray();
            bool unlimited = (capture.RendererFlags & 8) != 0;
            for (int y = 0; y < 224; y++)
            {
                var accepted = new List<OamPiece>(128);
                for (int rank = 0; rank < 128; rank++)
                {
                    var p = result.Pieces[(firstSlot + rank) % 128];
                    if (((y - p.Y) & 255) >= p.Size || p.X + p.Size <= 0) continue;
                    if (!unlimited && accepted.Count == 32) break;
                    accepted.Add(p);
                }
                int slivers = 0;
                for (int i = accepted.Count - 1; i >= 0; i--)
                {
                    var p = accepted[i]; int row = (y - p.Y) & 255;
                    for (int col = 0; col < p.Size; col += 8)
                    {
                        if (col + p.X <= -8 || col + p.X >= 256) continue;
                        if (++slivers > 34 && !unlimited) break;
                        for (int px = col; px < col + 8; px++)
                        {
                            int x = p.X + px;
                            if (x >= 0 && x < 256 && p.RawRgba[(row * p.Size + px) * 4 + 3] != 0) owner[y * 256 + x] = p.Slot;
                        }
                    }
                }
            }
            foreach (var p in result.Pieces)
                for (int row = 0; row < p.Size; row++) for (int col = 0; col < p.Size; col++)
                {
                    int x = p.X + col, y = (p.Y + row) & 255, offset = (row * p.Size + col) * 4;
                    if (x >= 0 && x < 256 && y < 224 && owner[y * 256 + x] == p.Slot) Buffer.BlockCopy(p.RawRgba, offset, p.VisibleRgba, offset, 4);
                }
            return result;
        }

        public static byte[] Composite(Reconstruction frame, bool requireSupported = true)
        {
            if (requireSupported && !frame.IsSupported) throw new NotSupportedException(string.Join("; ", frame.UnsupportedReasons));
            byte[] image = new byte[ScanoutReader.RgbaBytes];
            for (int i = 0; i < image.Length; i += 4) Pixel(image, i, frame.ForcedBlank ? new byte[] {0,0,0,255} : frame.Backdrop);
            if (frame.ForcedBlank) return image;
            var draw = new List<Tuple<int, Action>>();
            foreach (var bg in frame.Backgrounds.Where(b => b.MainEnabled)) draw.Add(Tuple.Create(bg.SortOrder, (Action)(() => {
                for (int i = 0; i < image.Length; i += 4) if (bg.Rgba[i + 3] != 0) Buffer.BlockCopy(bg.Rgba, i, image, i, 4);
            })));
            foreach (var p in frame.Pieces.Where(p => p.MainEnabled)) draw.Add(Tuple.Create(p.SortOrder, (Action)(() => {
                for (int row = 0; row < p.Size; row++) for (int col = 0; col < p.Size; col++) {
                    int x = p.X + col, y = (p.Y + row) & 255, offset = (row * p.Size + col) * 4;
                    if (x >= 0 && x < 256 && y < 224 && p.VisibleRgba[offset + 3] != 0) Buffer.BlockCopy(p.VisibleRgba, offset, image, (y * 256 + x) * 4, 4);
                }
            })));
            foreach (var item in draw.OrderBy(d => d.Item1)) item.Item2();
            return image;
        }
    }
}
