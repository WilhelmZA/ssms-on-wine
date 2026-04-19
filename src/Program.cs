using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

// ssms-patcher — applies the two binary patches to an SSMS 20 IDE directory
// so it runs under Wine.
//
// Usage:
//   ssms-patcher patch-gifs   <IDE_DIR>
//   ssms-patcher patch-nav    <IDE_DIR>
//   ssms-patcher restore      <IDE_DIR>
//   ssms-patcher verify       <IDE_DIR>

class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { PrintUsage(); return 2; }
        string cmd = args[0];
        string ideDir = args[1];
        if (!Directory.Exists(ideDir))
        {
            Console.Error.WriteLine($"IDE dir not found: {ideDir}");
            return 2;
        }
        try
        {
            return cmd switch
            {
                "patch-gifs" => PatchGifs(ideDir),
                "patch-nav"  => PatchNavigationService(ideDir),
                "restore"    => Restore(ideDir),
                "verify"     => Verify(ideDir),
                _ => Unknown(cmd)
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"fatal: {e.GetType().Name}: {e.Message}");
            Console.Error.WriteLine(e.StackTrace);
            return 1;
        }
    }

    static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        PrintUsage();
        return 2;
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine("usage: ssms-patcher <patch-gifs|patch-nav|restore|verify> <IDE_DIR>");
        Console.Error.WriteLine("  IDE_DIR = .../Microsoft SQL Server Management Studio 20/Common7/IDE");
    }

    // -----------------------------------------------------------------
    // patch-gifs: replace embedded GIF resources with PNG across all DLLs
    // -----------------------------------------------------------------
    static int PatchGifs(string ideDir)
    {
        Console.WriteLine($"scanning {ideDir} for DLLs with embedded GIFs...");
        int touched = 0, totalFound = 0, totalReplaced = 0;
        foreach (var dll in Directory.EnumerateFiles(ideDir, "*.dll", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(dll); } catch { continue; }
            if (!HasGifMagic(bytes)) continue;

            byte[] patched = ProcessDll(bytes, Path.GetFileName(dll), out int found, out int replaced);
            totalFound += found;
            if (replaced > 0)
            {
                string backup = dll + ".orig-gif";
                if (!File.Exists(backup)) File.Copy(dll, backup);
                File.WriteAllBytes(dll, patched);
                Console.WriteLine($"  [+] {Path.GetFileName(dll)}: {replaced}/{found} GIFs replaced");
                touched++;
                totalReplaced += replaced;
            }
        }
        Console.WriteLine($"\npatch-gifs complete: {touched} DLLs modified, {totalReplaced}/{totalFound} GIFs replaced.");
        return 0;
    }

    static bool HasGifMagic(byte[] d)
    {
        for (int i = 0; i < d.Length - 5; i++)
            if (d[i] == 'G' && d[i+1] == 'I' && d[i+2] == 'F' && d[i+3] == '8' &&
                (d[i+4] == '7' || d[i+4] == '9') && d[i+5] == 'a')
                return true;
        return false;
    }

    static byte[] ProcessDll(byte[] input, string name, out int found, out int replaced)
    {
        found = 0; replaced = 0;
        try
        {
            using var ms = new MemoryStream(input);
            using var asm = AssemblyDefinition.ReadAssembly(ms);
            var resources = asm.MainModule.Resources;
            bool any = false;
            for (int i = 0; i < resources.Count; i++)
            {
                if (resources[i] is EmbeddedResource er && er.Name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    var png = TryConvertGifToPng(er.GetResourceData(), int.MaxValue);
                    if (png != null)
                    {
                        resources[i] = new EmbeddedResource(er.Name, er.Attributes, png);
                        replaced++; found++; any = true;
                    }
                }
                else if (resources[i] is EmbeddedResource er2 && er2.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] raw = er2.GetResourceData();
                    byte[] rewritten = ByteLevelReplaceInPlace(raw, out int f1, out int r1);
                    found += f1;
                    if (r1 > 0)
                    {
                        resources[i] = new EmbeddedResource(er2.Name, er2.Attributes, rewritten);
                        replaced += r1; any = true;
                    }
                }
            }
            if (any)
            {
                using var outMs = new MemoryStream();
                asm.Write(outMs);
                return outMs.ToArray();
            }
            return input;
        }
        catch
        {
            // Cecil failed (strong-name resolution, complex reference chain, etc.)
            // Fall back to whole-DLL byte-level size-preserving replacement.
            return ByteLevelReplaceInPlace(input, out found, out replaced);
        }
    }

    static byte[] TryConvertGifToPng(byte[] gif, int maxSize)
    {
        if (gif.Length < 6 || gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F') return null;
        try
        {
            using var img = Image.Load(gif);

            byte[] try1;
            using (var m = new MemoryStream())
            {
                img.SaveAsPng(m, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
                try1 = m.ToArray();
            }
            if (try1.Length <= maxSize) return try1;

            byte[] try2;
            using (var m = new MemoryStream())
            {
                img.SaveAsPng(m, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression,
                    ColorType = PngColorType.Palette,
                    BitDepth = PngBitDepth.Bit8,
                    Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 256 })
                });
                try2 = m.ToArray();
            }
            if (try2.Length <= maxSize) return try2;

            byte[] try3;
            using (var m = new MemoryStream())
            {
                img.SaveAsPng(m, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression,
                    ColorType = PngColorType.Palette,
                    BitDepth = PngBitDepth.Bit4,
                    Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 16 })
                });
                try3 = m.ToArray();
            }
            if (try3.Length <= maxSize) return try3;
            return null;
        }
        catch { return null; }
    }

    static byte[] ByteLevelReplaceInPlace(byte[] data, out int found, out int replaced)
    {
        found = 0; replaced = 0;
        var result = new byte[data.Length];
        Array.Copy(data, result, data.Length);
        int i = 0;
        while (i < data.Length - 13)
        {
            if (data[i] == 'G' && data[i+1] == 'I' && data[i+2] == 'F' && data[i+3] == '8'
                && (data[i+4] == '7' || data[i+4] == '9') && data[i+5] == 'a')
            {
                found++;
                int end = FindGifEnd(data, i);
                if (end > i)
                {
                    int gifLen = end - i;
                    byte[] gifBytes = new byte[gifLen];
                    Array.Copy(data, i, gifBytes, 0, gifLen);
                    byte[] png = TryConvertGifToPng(gifBytes, gifLen);
                    if (png != null)
                    {
                        byte[] padded = PadPngToSize(png, gifLen);
                        if (padded != null && padded.Length == gifLen)
                        {
                            Array.Copy(padded, 0, result, i, gifLen);
                            i = end; replaced++; continue;
                        }
                    }
                }
            }
            i++;
        }
        return result;
    }

    static int FindGifEnd(byte[] data, int start)
    {
        int max = Math.Min(data.Length, start + 32768);
        for (int j = start + 13; j < max; j++) if (data[j] == 0x3B) return j + 1;
        return -1;
    }

    static byte[] PadPngToSize(byte[] png, int targetSize)
    {
        if (png.Length == targetSize) return png;
        if (png.Length > targetSize) return null;
        int pad = targetSize - png.Length;
        if (pad < 12) return null;
        int dataLen = pad - 12;
        int iendStart = png.Length - 12;
        if (!(png[iendStart+4] == 0x49 && png[iendStart+5] == 0x45 && png[iendStart+6] == 0x4E && png[iendStart+7] == 0x44)) return null;
        var chunk = new byte[pad];
        chunk[0] = (byte)((dataLen>>24)&0xFF); chunk[1] = (byte)((dataLen>>16)&0xFF);
        chunk[2] = (byte)((dataLen>>8)&0xFF); chunk[3] = (byte)(dataLen&0xFF);
        chunk[4] = (byte)'p'; chunk[5] = (byte)'r'; chunk[6] = (byte)'V'; chunk[7] = (byte)'t';
        uint crc = Crc32(chunk, 4, 4 + dataLen);
        chunk[8+dataLen] = (byte)((crc>>24)&0xFF); chunk[9+dataLen] = (byte)((crc>>16)&0xFF);
        chunk[10+dataLen] = (byte)((crc>>8)&0xFF); chunk[11+dataLen] = (byte)(crc&0xFF);
        var result = new byte[targetSize];
        Array.Copy(png, 0, result, 0, iendStart);
        Array.Copy(chunk, 0, result, iendStart, pad);
        Array.Copy(png, iendStart, result, iendStart + pad, 12);
        return result;
    }

    static readonly uint[] crcTable = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320 ^ (c>>1)) : (c>>1); t[n] = c; }
        return t;
    }
    static uint Crc32(byte[] data, int offset, int length)
    {
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < length; i++) c = crcTable[(c ^ data[offset+i]) & 0xFF] ^ (c>>8);
        return c ^ 0xFFFFFFFF;
    }

    // -----------------------------------------------------------------
    // patch-nav: no-op NavigationService.Initialize/GetView/GetEntity
    // in Microsoft.SqlServer.Management.SqlStudio.Explorer.dll
    // -----------------------------------------------------------------
    static int PatchNavigationService(string ideDir)
    {
        string target = Path.Combine(ideDir, "Microsoft.SqlServer.Management.SqlStudio.Explorer.dll");
        if (!File.Exists(target))
        {
            Console.Error.WriteLine($"target DLL not found: {target}");
            return 1;
        }
        string backup = target + ".preinject";
        if (!File.Exists(backup)) File.Copy(target, backup);
        File.Copy(backup, target, true);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(target));
        var rp = new ReaderParameters { InMemory = true, AssemblyResolver = resolver };
        using var asm = AssemblyDefinition.ReadAssembly(target, rp);
        var module = asm.MainModule;

        var navSvc = module.Types.FirstOrDefault(t =>
            t.FullName == "Microsoft.SqlServer.Management.SqlStudio.Explorer.NavigationService");
        if (navSvc == null)
        {
            Console.Error.WriteLine("NavigationService type not found");
            return 1;
        }

        int patched = 0;
        foreach (var m in navSvc.Methods)
        {
            if (!m.HasBody) continue;
            if (m.Name == "Initialize" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void")
            {
                m.Body = new MethodBody(m);
                m.Body.GetILProcessor().Emit(OpCodes.Ret);
                Console.WriteLine($"  [+] {m.Name} -> no-op");
                patched++;
            }
            else if ((m.Name == "GetView" || m.Name == "GetEntity") && m.Parameters.Count == 1)
            {
                m.Body = new MethodBody(m);
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                Console.WriteLine($"  [+] {m.Name}(string) -> null");
                patched++;
            }
        }

        asm.Write(target);
        Console.WriteLine($"\npatch-nav complete: {patched} method(s) rewritten in {Path.GetFileName(target)}");
        return 0;
    }

    // -----------------------------------------------------------------
    // restore: revert all patched files
    // -----------------------------------------------------------------
    static int Restore(string ideDir)
    {
        int count = 0;
        foreach (var bk in Directory.EnumerateFiles(ideDir, "*.orig-gif", SearchOption.AllDirectories))
        {
            string orig = bk.Substring(0, bk.Length - ".orig-gif".Length);
            File.Copy(bk, orig, true);
            File.Delete(bk);
            count++;
        }
        foreach (var bk in Directory.EnumerateFiles(ideDir, "*.preinject", SearchOption.AllDirectories))
        {
            string orig = bk.Substring(0, bk.Length - ".preinject".Length);
            File.Copy(bk, orig, true);
            File.Delete(bk);
            count++;
        }
        Console.WriteLine($"restored {count} DLLs");
        return 0;
    }

    // -----------------------------------------------------------------
    // verify: report which DLLs are currently patched
    // -----------------------------------------------------------------
    static int Verify(string ideDir)
    {
        var gifPatched = Directory.EnumerateFiles(ideDir, "*.orig-gif", SearchOption.AllDirectories).Count();
        var navPatched = Directory.EnumerateFiles(ideDir, "*.preinject", SearchOption.AllDirectories).Count();
        Console.WriteLine($"GIF-patched DLLs: {gifPatched}");
        Console.WriteLine($"Nav-patched DLLs: {navPatched}");
        return 0;
    }
}
