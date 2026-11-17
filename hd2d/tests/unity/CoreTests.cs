using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using X2HD2D.Core;

static class CoreTests
{
    static int assertions;
    static void Check(bool result, string label) { assertions++; if (!result) throw new Exception(label); }
    static void Reject(Action action, string label) { try { action(); } catch (CaptureFormatException) { assertions++; return; } throw new Exception("accepted " + label); }
    static void Write(byte[] b, int offset, uint value) { for(int i=0;i<4;i++) b[offset+i]=(byte)(value>>(i*8)); }
    static void Rehash(byte[] b) { using(var sha=SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(b,0,b.Length-32),0,b,b.Length-32,32); }
    static int Main(string[] args)
    {
        string root=args[0];
        Check(Directory.GetFiles(root,"*.x2scan").Length==19,"complete synthetic fixture suite (18 static + raster)");
        foreach (string path in Directory.GetFiles(root,"*.x2scan").OrderBy(p=>p))
        {
            var capture=ScanoutReader.ReadFile(path);
            var decoded=GraphicsDecoder.Decode(capture);
            if(Path.GetFileName(path)=="raster-existing.x2scan")
            {
                Check(!decoded.IsSupported,"raster fixture must be marked incomplete");
                Check(decoded.UnsupportedReasons.Any(s=>s.StartsWith("Raster")),"raster diagnostics");
                Check(decoded.UnsupportedReasons.Any(s=>s.StartsWith("Color math")),"color math diagnostics");
                bool refused=false; try { GraphicsDecoder.Composite(decoded); } catch(NotSupportedException) { refused=true; }
                Check(refused,"strict comparison refuses incomplete effects");
                Check(GraphicsDecoder.Decode(capture,128).State.Line==128,"explicit scanline inspection");
                continue;
            }
            Check(decoded.IsSupported,Path.GetFileName(path)+": "+string.Join(";",decoded.UnsupportedReasons));
            byte[] actual=GraphicsDecoder.Composite(decoded), reference=capture.CopyReferenceRgba();
            int different=Enumerable.Range(0,256*224).Count(p=>Enumerable.Range(0,4).Any(c=>actual[p*4+c]!=reference[p*4+c]));
            Console.WriteLine(Path.GetFileName(path)+": different_pixels="+different);
            if(different>0) File.WriteAllBytes(path+".managed.rgba",actual);
            Check(different==0, "native oracle parity: "+path);
            Check(decoded.Backgrounds.Length==6 && decoded.Pieces.Length==128,"independent components");
            Check(decoded.Backgrounds[4].BitsPerPixel==2 && decoded.Backgrounds[0].BitsPerPixel==4,"Mode 1 bpp");
            if(Path.GetFileName(path)=="palette-flips.x2scan") {
                var bg=decoded.Backgrounds[0];
                Check(bg.MapWidth==64 && bg.MapHeight==64 && bg.ScrollX==507 && bg.ScrollY==509,"map/scroll metadata");
                Check((bg.Tilemap[3]&0xc000)==0xc000,"tile flip attributes retained");
            }
            if(Path.GetFileName(path)=="obj-wrap.x2scan") {
                var p=decoded.Pieces[0];
                Check(p.X==-6 && p.Y==250 && p.Large && p.FlipX && p.FlipY,"OAM position/flip/high table metadata");
            }
            if(Path.GetFileName(path)=="obj-rotation.x2scan")
                Check(decoded.Pieces[1].EvaluationOrder==0 && decoded.Pieces[0].EvaluationOrder==127,"OAM rotation metadata");
        }
        byte[] original=File.ReadAllBytes(Path.Combine(root,"static8.x2scan"));
        var immutable=ScanoutReader.Decode(original);
        byte[] originalCopy=(byte[])original.Clone(); original[ScanoutReader.MemoryOffset+12]^=255;
        Check(immutable.Lines[1].Vram.Byte(0)==originalCopy[ScanoutReader.MemoryOffset+12],"owned memory immutable");
        byte[] pixels=immutable.CopyReferenceRgba(); pixels[0]^=255;
        Check(pixels[0]!=immutable.CopyReferenceRgba()[0],"reference copy isolation");
        byte[] changedReference=(byte[])originalCopy.Clone();
        changedReference[ScanoutReader.ReferenceOffset]^=255; Rehash(changedReference);
        var altered=ScanoutReader.Decode(changedReference);
        Check(GraphicsDecoder.Composite(GraphicsDecoder.Decode(altered)).SequenceEqual(immutable.CopyReferenceRgba()),
            "reconstruction must not consume reference pixels");
        foreach(var mutation in new[]{(8,2u),(12,2u),(16,1u),(20,224u),(32,69u),(176,2u),(180,226u),
            (ScanoutReader.HeaderBytes+ScanoutReader.LineBytes,99u),
            (ScanoutReader.HeaderBytes+4,1u), (ScanoutReader.HeaderBytes+16,256u),
            (ScanoutReader.HeaderBytes+16+4*4,7u),(ScanoutReader.MemoryOffset,4u),(ScanoutReader.MemoryOffset+8,1u)})
        {
            byte[] bad=(byte[])originalCopy.Clone(); Write(bad,mutation.Item1,mutation.Item2); Rehash(bad);
            Reject(()=>ScanoutReader.Decode(bad),"malformed offset "+mutation.Item1);
        }
        Reject(()=>ScanoutReader.Decode(new byte[ScanoutReader.MaxBytes+1]),"over budget");
        Reject(()=>ScanoutReader.Decode(originalCopy.Take(originalCopy.Length-1).ToArray()),"truncated");
        byte[] corrupted=(byte[])originalCopy.Clone(); corrupted[300]^=1; Reject(()=>ScanoutReader.Decode(corrupted),"checksum");
        var s=immutable.Lines[1];
        Check(GraphicsDecoder.PlanarPixel(s.Vram,0x1000,0,4,0,0)==1,"independent planar bit order");
        Check(GraphicsDecoder.PaletteColor(s.Cgram,1,15).SequenceEqual(new byte[]{255,0,0,255}),"RGB15 expansion");
        Check(GraphicsDecoder.PaletteColor(s.Cgram,1,7)[0]==119,"integer brightness");
        Console.WriteLine("Managed scanout/core checks: PASS ("+assertions+" assertions)");
        return 0;
    }
}
