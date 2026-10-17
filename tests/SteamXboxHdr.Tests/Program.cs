using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamXboxHdr;

internal static class Program
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException or System.IO.FileFormatException) { rejected = true; }
        Check(rejected, name);
    }
    static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    [STAThread]
    static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamXboxHdr.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var steamRoot = Path.Combine(root, "Steam");
            var shotDir = Path.Combine(steamRoot, "userdata", "222", "760", "remote", "1888930", "screenshots");
            Directory.CreateDirectory(shotDir);
            var captures = Path.Combine(root, "captures"); Directory.CreateDirectory(captures);
            var jpg = Path.Combine(shotDir, "20261015153000_1.jpg"); File.WriteAllBytes(jpg, [1, 2, 3, 4]);
            var hdr = Path.Combine(captures, "Game capture.jxr");
            // A real WIC JPEG XR with values above SDR white; generated solely in the test directory.
            float[] pixels = [2f, 1.5f, .5f, 1f, 3f, 2f, 1f, 1f, .2f, .3f, .4f, 1f, 4f, 3f, 2f, 1f];
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgba128Float, null, pixels, 32);
            var encoder = new WmpBitmapEncoder { ImageQualityLevel = 1 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(hdr)) encoder.Save(file);
            var png = Path.ChangeExtension(hdr, ".png");
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0)));
            using (var file = File.Create(png)) pngEncoder.Save(file);
            HdrImage.Validate(hdr); Check(true, "WIC decodes actual floating-point JXR fixture");
            var pair = ShotFiles.Match(steamRoot, captures, [jpg, hdr, png]);
            Check(pair.Steam.Account == "222" && pair.Steam.Game == "1888930", "account and game come from new screenshot path");
            Check(pair.Sdr == png, "same-stem PNG is paired");
            Check(ShotFiles.Steam(steamRoot, Path.Combine(shotDir, "thumbnails", "a.jpg")) == null, "thumbnail excluded");
            Check(ShotFiles.Steam(steamRoot, Path.Combine(shotDir, "Xbox HDR", "a.png")) == null, "archive excluded");
            Check(ShotFiles.Steam(steamRoot, Path.Combine(root, "outside.jpg")) == null, "outside path excluded");
            Check(ShotFiles.Scan(steamRoot, captures).Count == 3, "scan sees only direct image candidates");
            Reject(() => ShotFiles.Match(steamRoot, captures, [hdr, png]), "missing Steam output rejected");
            Reject(() => ShotFiles.Match(steamRoot, captures, [jpg, png]), "SDR-only output rejected");
            var second = jpg.Replace("222", "333");
            Reject(() => ShotFiles.Match(steamRoot, captures, [jpg, second, hdr]), "multiple accounts rejected rather than guessed");
            Reject(() => ShotFiles.Match(steamRoot, captures, [jpg, hdr, Path.Combine(captures, "second.jxr")]), "multiple JXR candidates rejected");
            Check(ShotFiles.Match(steamRoot, captures, [jpg, hdr, Path.Combine(captures, "different.png")]).Sdr == null, "unrelated PNG is never paired");
            var renamed = Path.Combine(captures, "renamed.jxr"); File.Copy(png, renamed);
            Reject(() => HdrImage.Validate(renamed), "PNG renamed to JXR rejected");
            var truncated = Path.Combine(captures, "truncated.jxr"); File.WriteAllBytes(truncated, [0x49, 0x49, 0xbc, 0x01]);
            Reject(() => HdrImage.Validate(truncated), "truncated JXR rejected");
            var hashes = new[] { jpg, hdr, png }.ToDictionary(p => p, Hash);
            var context = new CaptureContext(DateTimeOffset.UtcNow, 0, 1234, "FixtureGame", new("fixture", true, "test"));
            var archive = ArchiveWriter.Write(pair, context, true, HdrImage.Validate);
            Check(archive == shotDir, "Xbox files are placed directly beside the Steam JPG");
            Check(Directory.GetFiles(archive, "*.jxr").Length == 1 && Directory.GetFiles(archive, "*.png").Length == 1, "HDR and SDR both archived");
            Check(Hash(Directory.GetFiles(archive, "*.jxr").Single()) == hashes[hdr], "HDR archive is byte-for-byte identical");
            Check(Hash(Directory.GetFiles(archive, "*.png").Single()) == hashes[png], "SDR archive is byte-for-byte identical");
            Check(hashes.All(p => File.Exists(p.Key) && Hash(p.Key) == p.Value), "all original files remain unchanged");
            Check(Directory.GetFiles(archive, "*.png").All(p => ShotFiles.Steam(steamRoot, p) == null), "archived PNG cannot be mistaken for a new Steam screenshot");
            Check(ShotFiles.Scan(steamRoot, captures).Count(p => ShotFiles.Steam(steamRoot, p) != null) == 1, "same-folder archive does not feed back into screenshot detection");
            var metadata = Directory.GetFiles(Path.Combine(archive, ".metadata"), "*.json").Single();
            using (var json = JsonDocument.Parse(File.ReadAllText(metadata)))
                Check(json.RootElement.GetProperty("files")[0].GetProperty("Sha256").GetString() == hashes[hdr], "metadata contains verified source hash");
            Check((File.GetAttributes(Path.GetDirectoryName(metadata)!) & FileAttributes.Hidden) != 0, "metadata directory is hidden");
            ArchiveWriter.Write(pair, context, false, HdrImage.Validate);
            Check(Directory.GetFiles(archive, "*.jxr").Length == 2 && Directory.GetFiles(archive, "*.png").Length == 1, "repeat never overwrites, SDR option respected");
            var before = Directory.GetFiles(archive, "*", SearchOption.AllDirectories).Order().ToArray();
            using (var locked = new FileStream(png, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => ArchiveWriter.Write(pair, context, true, HdrImage.Validate), "locked source aborts archive");
            Check(before.SequenceEqual(Directory.GetFiles(archive, "*", SearchOption.AllDirectories).Order()), "failed copy rolls back its own partial files");
            Check(hashes.All(p => Hash(p.Key) == p.Value), "originals intact after failure");
            Check(Marshal.SizeOf<Native.Header>() == 20 && Marshal.SizeOf<Native.ColorInfo>() == 36 && Marshal.SizeOf<Native.DisplayPath>() == 72, "display interop structure sizes match Windows SDK");
            Check(Marshal.SizeOf<Native.Input>() == 40, "SendInput x64 structure is 40 bytes");
            using (var passiveHook = new ScreenshotHook()) Check(!passiveHook.Enabled, "keyboard hook installs without elevation and starts disabled");
            var apps = Path.Combine(steamRoot, "steamapps"); Directory.CreateDirectory(apps);
            var external = Path.Combine(root, "External Library"); Directory.CreateDirectory(Path.Combine(external, "steamapps"));
            File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + external.Replace("\\", "\\\\") + "\" } }");
            File.WriteAllText(Path.Combine(external, "steamapps", "appmanifest_42.acf"), "\"AppState\" { \"appid\" \"42\" \"installdir\" \"Test Game\" }");
            File.WriteAllText(Path.Combine(apps, "appmanifest_99.acf"), "\"AppState\" { \"installdir\" \"..\\\\Outside\" }");
            var games = SteamGames.Read(steamRoot);
            Check(games.Count == 1 && games[0].AppId == "42", "external library detected and traversal manifest rejected");
            Check(SteamGames.Find(games, Path.Combine(external, "steamapps", "common", "Test Game", "bin", "game.exe"))?.AppId == "42", "foreground executable maps to installed Steam game");
            Check(SteamGames.Find(games, Path.Combine(external, "steamapps", "common", "Test Game other", "browser.exe")) == null, "similar directory prefix cannot impersonate game");
            foreach (var realSample in args)
            {
                var sourceHash = Hash(realSample);
                HdrImage.Validate(realSample);
                Check(true, "real Game Bar half-float JXR accepted: " + Path.GetFileName(realSample));
                var realPair = pair with { Hdr = realSample, Sdr = null };
                var prior = Directory.GetFiles(shotDir, "*.jxr").ToHashSet();
                ArchiveWriter.Write(realPair, context, false, HdrImage.Validate, manual: true);
                var copy = Directory.GetFiles(shotDir, "*.jxr").Single(p => !prior.Contains(p));
                Check(Hash(copy) == sourceHash && Hash(realSample) == sourceHash, "real sample copied intact beside Steam JPG");
            }
            Console.WriteLine($"{passed} tests passed. Live Game Bar was not triggered.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
