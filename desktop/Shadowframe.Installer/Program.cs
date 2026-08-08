using System.Diagnostics;
using System.Formats.Tar;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Shadowframe.Installer;

internal static class Program
{
    public const string ProductName = "Shadowframe AI";
    public const string ProductVersion = "0.3.6";
    public const string Publisher = "Shadowframe AI";
    public const string PayloadName = "Shadowframe-Core.tar";
    public const string ManifestName = "Shadowframe-Package.json";
    public const string ReleaseProfileName = "Shadowframe-ReleaseProfile.json";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ShadowframeAI";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, ModelPackApplication.ManifestName)))
            return ModelPackApplication.Run(args);

        var options = InstallOptions.Parse(args);
        try
        {
            if (options.Uninstall)
                return InstallerEngine.Uninstall(options);

            if (options.Silent)
            {
                var checks = PrerequisiteChecker.Run(options, LoadManifest());
                if (checks.Blockers.Count > 0 && !options.AllowUnsupported)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, checks.Blockers));
                InstallerEngine.Install(options, LoadManifest(), null);
                return 0;
            }

            Application.Run(new InstallerForm(options, LoadManifest()));
            return Environment.ExitCode;
        }
        catch (Exception exception)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "Shadowframe-Setup.log"), $"[{DateTimeOffset.Now:O}] {exception}\r\n");
            }
            catch { }
            if (!options.Silent)
                MessageBox.Show(exception.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                Console.Error.WriteLine(exception);
            return 1;
        }
    }

    public static PackageManifest LoadManifest()
    {
        return JsonSerializer.Deserialize<PackageManifest>(InstallerAssets.ReadText(ManifestName), JsonOptions.Default)
               ?? throw new InvalidDataException("The Shadowframe package manifest is invalid.");
    }

    public static string LoadReleaseProfile()
    {
        try
        {
            var document = JsonDocument.Parse(InstallerAssets.ReadText(ReleaseProfileName, required: false) ?? "{ \"profile\": \"creator\" }");
            if (document.RootElement.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.String)
            {
                var value = profile.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch { }
        return "creator";
    }
}

internal static class InstallerAssets
{
    private const string ResourcePrefix = "InstallerAssets/";

    public static string ReadText(string fileName, bool required = true)
    {
        var bytes = ReadBytes(fileName, required);
        if (bytes is null) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    public static byte[]? ReadBytes(string fileName, bool required = true)
    {
        var diskPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(diskPath))
            return File.ReadAllBytes(diskPath);

        using var stream = typeof(InstallerAssets).Assembly.GetManifestResourceStream(ResourcePrefix + fileName.Replace('\\', '/'));
        if (stream is null)
        {
            if (required) throw new FileNotFoundException($"{fileName} was not found in the setup package.", diskPath);
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

internal sealed record PackageManifest(string Version, string PayloadFile, string Sha256, long UncompressedBytes, int FileCount, string? PayloadUrl = null);

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}

internal sealed class InstallOptions
{
    public bool Silent { get; init; }
    public bool Uninstall { get; init; }
    public bool Detached { get; init; }
    public bool RemoveData { get; init; }
    public bool DesktopShortcut { get; init; } = true;
    public bool NoShortcuts { get; init; }
    public bool AllowUnsupported { get; init; }
    public bool InstallModelPacks { get; init; } = true;
    public string InstallDirectory { get; init; } = DefaultInstallDirectory();
    public string DataRoot { get; init; } = DefaultDataRoot();
    public string OutputRoot { get; init; } = DefaultOutputRoot();

    public static InstallOptions Parse(IEnumerable<string> args)
    {
        var values = args.ToArray();
        var publicRelease = Program.LoadReleaseProfile().Equals("public", StringComparison.OrdinalIgnoreCase);
        string? installDirectory = values.FirstOrDefault(value => value.StartsWith("/INSTALLDIR=", StringComparison.OrdinalIgnoreCase));
        string? dataRoot = values.FirstOrDefault(value => value.StartsWith("/DATAROOT=", StringComparison.OrdinalIgnoreCase));
        string? outputRoot = values.FirstOrDefault(value => value.StartsWith("/OUTPUTROOT=", StringComparison.OrdinalIgnoreCase));
        var resolvedInstallDirectory = installDirectory is null
            ? DefaultInstallDirectory(publicRelease)
            : Path.GetFullPath(installDirectory[(installDirectory.IndexOf('=') + 1)..].Trim('"'));
        var resolvedDataRoot = publicRelease
            ? resolvedInstallDirectory
            : dataRoot is null
                ? DefaultDataRoot()
                : Path.GetFullPath(dataRoot[(dataRoot.IndexOf('=') + 1)..].Trim('"'));
        var resolvedOutputRoot = publicRelease
            ? resolvedInstallDirectory
            : outputRoot is null
                ? DefaultOutputRoot()
                : Path.GetFullPath(outputRoot[(outputRoot.IndexOf('=') + 1)..].Trim('"'));
        return new InstallOptions
        {
            Silent = values.Any(value => value.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) || value.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase)),
            Uninstall = values.Any(value => value.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase)),
            Detached = values.Any(value => value.Equals("/DETACHED", StringComparison.OrdinalIgnoreCase)),
            RemoveData = values.Any(value => value.Equals("/REMOVEDATA", StringComparison.OrdinalIgnoreCase)),
            DesktopShortcut = !values.Any(value => value.Equals("/NODESKTOP", StringComparison.OrdinalIgnoreCase)),
            NoShortcuts = values.Any(value => value.Equals("/NOSHORTCUTS", StringComparison.OrdinalIgnoreCase)),
            AllowUnsupported = values.Any(value => value.Equals("/ALLOWUNSUPPORTED", StringComparison.OrdinalIgnoreCase)),
            InstallModelPacks = !values.Any(value => value.Equals("/NOMODELPACKS", StringComparison.OrdinalIgnoreCase)),
            InstallDirectory = resolvedInstallDirectory,
            DataRoot = resolvedDataRoot,
            OutputRoot = resolvedOutputRoot
        };
    }

    private static string DefaultInstallDirectory(bool publicRelease = false)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Program.UninstallKey);
        return key?.GetValue("InstallLocation") is string existing && !string.IsNullOrWhiteSpace(existing)
            ? existing
            : publicRelease
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shadowframe AI")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Shadowframe AI");
    }

    private static string DefaultDataRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Program.UninstallKey);
        return key?.GetValue("DataRoot") is string existing && !string.IsNullOrWhiteSpace(existing)
            ? existing
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shadowframe");
    }

    private static string DefaultOutputRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Program.UninstallKey);
        return key?.GetValue("OutputRoot") is string existing && !string.IsNullOrWhiteSpace(existing)
            ? existing
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Shadowframe Output");
    }
}

internal sealed record PrerequisiteResults(List<string> Passed, List<string> Warnings, List<string> Blockers)
{
    public string DisplayText => string.Join(Environment.NewLine, new[]
    {
        string.Join(Environment.NewLine, Passed.Select(item => $"✓ {item}")),
        string.Join(Environment.NewLine, Warnings.Select(item => $"! {item}")),
        string.Join(Environment.NewLine, Blockers.Select(item => $"× {item}"))
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

internal static class PrerequisiteChecker
{
    public static PrerequisiteResults Run(InstallOptions options, PackageManifest manifest)
    {
        var passed = new List<string>();
        var warnings = new List<string>();
        var blockers = new List<string>();

        if (Environment.Is64BitOperatingSystem && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            passed.Add("Supported 64-bit Windows version");
        else
            blockers.Add("Windows 10 version 2004 or newer (64-bit) is required.");

        if (HasNvidiaAdapter()) passed.Add("NVIDIA graphics adapter detected");
        else blockers.Add("A supported NVIDIA graphics adapter and current driver are required.");

        if (HasWebView2()) passed.Add("Microsoft Edge WebView2 Runtime detected");
        else blockers.Add("Microsoft Edge WebView2 Runtime is required. Install it from Microsoft, then run Setup again.");

        var root = Path.GetPathRoot(Path.GetFullPath(options.InstallDirectory)) ?? "C:\\";
        var drive = new DriveInfo(root);
        var required = manifest.UncompressedBytes + 5L * 1024 * 1024 * 1024;
        var releaseProfile = Program.LoadReleaseProfile();
        if (releaseProfile.Equals("public", StringComparison.OrdinalIgnoreCase) && options.InstallModelPacks)
        {
            required += InstallerEngine.GetRequiredPublicPackFreeSpace();
            warnings.Add($"Setup will automatically download about {FormatBytes(InstallerEngine.GetPublicPackDownloadBytes())} of required public model packs and reserve extra space while installing.");
        }

        if (drive.AvailableFreeSpace >= required)
            passed.Add($"Enough disk space ({FormatBytes(drive.AvailableFreeSpace)} available)");
        else
            blockers.Add($"At least {FormatBytes(required)} of free space is required on {drive.Name}.");

        if (File.Exists(Path.Combine(options.InstallDirectory, "Shadowframe.exe")))
            warnings.Add("An existing installation will be repaired or updated; models and generations are preserved.");

        return new PrerequisiteResults(passed, warnings, blockers);
    }

    private static bool HasWebView2()
    {
        const string client = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
            using var key = baseKey.OpenSubKey(client);
            if (key?.GetValue("pv") is string version && !string.IsNullOrWhiteSpace(version) && version != "0.0.0.0") return true;
        }
        return false;
    }

    private static bool HasNvidiaAdapter()
    {
        const string videoKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(videoKey);
        if (root is null) return false;
        foreach (var name in root.GetSubKeyNames())
        {
            using var adapter = root.OpenSubKey(name);
            var description = string.Join(" ", adapter?.GetValue("DriverDesc"), adapter?.GetValue("ProviderName"));
            if (description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
}

internal sealed record InstallProgress(int Percent, string Message);

internal static class InstallerEngine
{
    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly PublicModelPackDefinition[] PublicModelPacks =
    {
        new(
            "anima-public",
            "Anima public models",
            "Install Shadowframe Anima Public Models.exe",
            "TheBaldDudeCo/shadowframe-anima-public-models",
            6052657990),
        new(
            "wan-public",
            "Wan public models",
            "Install Shadowframe Wan Public Models.exe",
            "TheBaldDudeCo/shadowframe-wan-public-models",
            69074964839),
        new(
            "photoreal-public",
            "PhotoReal public models",
            "Install Shadowframe PhotoReal Public Models.exe",
            "TheBaldDudeCo/shadowframe-photoreal-public-models",
            72870622674)
    };

    public static long GetPublicPackDownloadBytes() => PublicModelPacks.Sum(pack => pack.PayloadBytes);

    public static long GetRequiredPublicPackFreeSpace()
    {
        const long extraHeadroomBytes = 8L * 1024 * 1024 * 1024;
        return PublicModelPacks.Sum(pack => pack.PayloadBytes * 2) + extraHeadroomBytes;
    }

    public static void Install(InstallOptions options, PackageManifest manifest, IProgress<InstallProgress>? progress)
    {
        progress?.Report(new(1, "Preparing the Core package…"));
        var payloadPath = MaterializePayload(manifest, progress);

        progress?.Report(new(2, "Verifying the Core package…"));
        try
        {
            using (var stream = File.OpenRead(payloadPath))
            {
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The Core package failed its integrity check. Download the installer again.");
            }

            var installRoot = Path.GetFullPath(options.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(installRoot)?.FullName ?? throw new InvalidOperationException("The installation folder is invalid.");
            Directory.CreateDirectory(parent);
            StopInstalledRuntime(installRoot);

            var staging = Path.Combine(parent, $".shadowframe-staging-{Guid.NewGuid():N}");
            var backup = Path.Combine(parent, $".shadowframe-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var replacedExisting = false;
            try
            {
                progress?.Report(new(8, "Installing the private AI runtime…"));
                ExtractPayload(payloadPath, staging, manifest.FileCount, progress);
                ValidateStaging(staging);

                if (Directory.Exists(installRoot))
                {
                    Directory.Move(installRoot, backup);
                    replacedExisting = true;
                }
                Directory.Move(staging, installRoot);

                var uninstaller = Path.Combine(installRoot, "Shadowframe Uninstaller.exe");
                File.Copy(Environment.ProcessPath!, uninstaller, true);
                File.WriteAllText(Path.Combine(installRoot, "install-receipt.json"), JsonSerializer.Serialize(new
                {
                    product = Program.ProductName,
                    version = manifest.Version,
                    installedAt = DateTimeOffset.Now,
                    dataRoot = Path.GetFullPath(options.DataRoot),
                    outputRoot = Path.GetFullPath(options.OutputRoot)
                }, JsonOptions.Default));

                if (!options.NoShortcuts) CreateShortcuts(installRoot, options.DesktopShortcut);
                WriteUninstallRegistration(installRoot, manifest, options);
                PrepareDataFolders(options);
                CopySamplePrompts(options.DataRoot);
                if (options.InstallModelPacks)
                    InstallAdjacentModelPacks(options, progress);
                progress?.Report(new(100, "Shadowframe AI is ready."));

                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                if (replacedExisting && Directory.Exists(backup))
                {
                    if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
                    Directory.Move(backup, installRoot);
                }
                throw;
            }
        }
        finally
        {
            DeleteMaterializedPayload(payloadPath);
        }
    }

    private static string MaterializePayload(PackageManifest manifest, IProgress<InstallProgress>? progress)
    {
        var sidecarPath = Path.Combine(AppContext.BaseDirectory, manifest.PayloadFile);
        if (File.Exists(sidecarPath)) return sidecarPath;

        var embeddedBytes = InstallerAssets.ReadBytes(manifest.PayloadFile, required: false);
        if (embeddedBytes is { Length: > 0 })
        {
            var embeddedTempPath = Path.Combine(Path.GetTempPath(), $"shadowframe-core-{Guid.NewGuid():N}.tar");
            File.WriteAllBytes(embeddedTempPath, embeddedBytes);
            return embeddedTempPath;
        }

        if (string.IsNullOrWhiteSpace(manifest.PayloadUrl))
            throw new FileNotFoundException("The Shadowframe Core package is missing and no download source was included with Setup.", sidecarPath);

        progress?.Report(new(3, "Downloading the Shadowframe Core runtime…"));
        var downloadPath = Path.Combine(Path.GetTempPath(), $"shadowframe-core-download-{Guid.NewGuid():N}.tar");
        DownloadFile(manifest.PayloadUrl!, downloadPath, progress);
        return downloadPath;
    }

    private static void DeleteMaterializedPayload(string payloadPath)
    {
        try
        {
            if (!payloadPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(payloadPath))
                File.Delete(payloadPath);
        }
        catch { }
    }

    private static void DownloadFile(string url, string destinationPath, IProgress<InstallProgress>? progress)
    {
        using var response = DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;

        using var responseStream = response.Content.ReadAsStream();
        using var fileStream = File.Create(destinationPath);
        var buffer = new byte[1024 * 1024];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            fileStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
            if (totalBytes is > 0)
            {
                var percent = 3 + (int)Math.Min(4, Math.Round(totalRead / (double)totalBytes.Value * 4));
                progress?.Report(new(percent, $"Downloading the Shadowframe Core runtime… {Math.Round(totalRead / 1024d / 1024d):N0} MB of {Math.Round(totalBytes.Value / 1024d / 1024d):N0} MB"));
            }
        }
    }

    private static void ExtractPayload(string payloadPath, string destination, int expectedFiles, IProgress<InstallProgress>? progress)
    {
        using var stream = File.OpenRead(payloadPath);
        using var archive = new TarReader(stream);
        var completed = 0;
        while (archive.GetNextEntry() is { } entry)
        {
            var normalizedName = entry.Name.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalizedName)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, normalizedName));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Core payload contains an unsafe path.");
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            completed++;
            if (completed % 250 == 0)
                progress?.Report(new(8 + (int)(Math.Min(completed, expectedFiles) / (double)Math.Max(expectedFiles, 1) * 86), $"Installing Core files… {completed:N0} of {expectedFiles:N0}"));
        }
        if (expectedFiles > 0 && completed < expectedFiles) throw new InvalidDataException("The Core payload is incomplete.");
    }

    private static void ValidateStaging(string staging)
    {
        foreach (var relative in new[] { "Shadowframe.exe", "runtime-manifest.json", @"Runtime\ComfyUI\main.py", @"Runtime\PythonBase\python.exe", @"Runtime\Node\node.exe", @"scripts\Start-Shadowframe-Core.ps1" })
            if (!File.Exists(Path.Combine(staging, relative))) throw new InvalidDataException($"The installed Core is missing {relative}.");
    }

    private static void StopInstalledRuntime(string installRoot)
    {
        var stopScript = Path.Combine(installRoot, "scripts", "Stop-Shadowframe-Core.ps1");
        if (!File.Exists(stopScript)) return;
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{stopScript}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        process?.WaitForExit(30000);
    }

    private static void CreateShortcuts(string installRoot, bool desktop)
    {
        var executable = Path.Combine(installRoot, "Shadowframe.exe");
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Shadowframe AI");
        Directory.CreateDirectory(startMenu);
        Shortcut.Create(Path.Combine(startMenu, "Shadowframe AI.lnk"), executable, installRoot, "Private local AI creation");
        Shortcut.Create(Path.Combine(startMenu, "Uninstall Shadowframe AI.lnk"), Path.Combine(installRoot, "Shadowframe Uninstaller.exe"), installRoot, "Remove Shadowframe AI", "/UNINSTALL");
        var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Shadowframe AI.lnk");
        if (desktop) Shortcut.Create(desktopPath, executable, installRoot, "Private local AI creation");
        else if (File.Exists(desktopPath)) File.Delete(desktopPath);
    }

    private static void WriteUninstallRegistration(string installRoot, PackageManifest manifest, InstallOptions options)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Program.UninstallKey);
        var uninstaller = Path.Combine(installRoot, "Shadowframe Uninstaller.exe");
        key.SetValue("DisplayName", Program.ProductName);
        key.SetValue("DisplayVersion", manifest.Version);
        key.SetValue("Publisher", Program.Publisher);
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("DataRoot", Path.GetFullPath(options.DataRoot));
        key.SetValue("OutputRoot", Path.GetFullPath(options.OutputRoot));
        key.SetValue("DisplayIcon", $"{Path.Combine(installRoot, "Shadowframe.exe")},0");
        key.SetValue("UninstallString", $"\"{uninstaller}\" /UNINSTALL /INSTALLDIR=\"{installRoot}\"");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /UNINSTALL /SILENT /INSTALLDIR=\"{installRoot}\"");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, manifest.UncompressedBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void PrepareDataFolders(InstallOptions options)
    {
        var dataRoot = Path.GetFullPath(options.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var outputRoot = Path.GetFullPath(options.OutputRoot).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var path in new[]
        {
            dataRoot,
            Path.Combine(dataRoot, "models"),
            Path.Combine(dataRoot, "State"),
            Path.Combine(outputRoot, "input"),
            Path.Combine(outputRoot, "output"),
            Path.Combine(outputRoot, "temp")
        })
        {
            Directory.CreateDirectory(path);
        }
    }

    private static void CopySamplePrompts(string dataRoot)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Sample Prompts");
        var target = Path.Combine(Path.GetFullPath(dataRoot), "Sample Prompts");
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            CopyDirectory(source, target);
            return;
        }

        var promptArchive = InstallerAssets.ReadBytes("Sample-Prompts.zip", required: false);
        if (promptArchive is null || promptArchive.Length == 0) return;

        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var tempArchive = Path.Combine(Path.GetTempPath(), $"shadowframe-prompts-{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllBytes(tempArchive, promptArchive);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempArchive, target, overwriteFiles: true);
        }
        finally
        {
            try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void InstallAdjacentModelPacks(InstallOptions options, IProgress<InstallProgress>? progress)
    {
        var packs = DiscoverModelPackInstallers().ToList();
        if (packs.Count == 0 && Program.LoadReleaseProfile().Equals("public", StringComparison.OrdinalIgnoreCase))
            packs = AcquirePublicModelPackInstallers(options, progress).ToList();
        if (packs.Count == 0) return;
        for (var index = 0; index < packs.Count; index++)
        {
            var pack = packs[index];
            progress?.Report(new(92 + (int)(index / (double)Math.Max(packs.Count, 1) * 7), $"Installing model pack {index + 1} of {packs.Count}: {Path.GetFileNameWithoutExtension(pack)}…"));
            var arguments = $"/SILENT /ALLOWUNSUPPORTED /DATAROOT=\"{Path.GetFullPath(options.DataRoot)}\"";
            using var process = Process.Start(new ProcessStartInfo(pack, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(pack)!
            });
            process?.WaitForExit();
            if (process is not null && process.ExitCode != 0)
                throw new InvalidOperationException($"{Path.GetFileName(pack)} failed. Check %TEMP%\\Shadowframe-ModelPack-Setup.log for details.");
        }
    }

    private static IEnumerable<string> DiscoverModelPackInstallers()
    {
        var roots = new List<string> { AppContext.BaseDirectory };
        var parent = Directory.GetParent(AppContext.BaseDirectory);
        if (parent is not null) roots.Add(parent.FullName);

        var preferredOrder = new[] { "Anima", "Wan", "PhotoReal" };
        var candidates = roots
            .Where(Directory.Exists)
            .SelectMany(root =>
            {
                try { return Directory.EnumerateFiles(root, "Install Shadowframe * Models.exe", SearchOption.AllDirectories); }
                catch { return Enumerable.Empty<string>(); }
            })
            .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.OrderBy(path =>
        {
            var name = Path.GetFileName(path);
            var match = Array.FindIndex(preferredOrder, item => name.Contains(item, StringComparison.OrdinalIgnoreCase));
            return match < 0 ? preferredOrder.Length : match;
        }).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> AcquirePublicModelPackInstallers(InstallOptions options, IProgress<InstallProgress>? progress)
    {
        var enabledPackIds = LoadRequestedPublicPackIds();
        var cacheRoot = Path.Combine(Path.GetFullPath(options.DataRoot), "InstallCache", "PublicModelPacks", $"core-{Program.ProductVersion}");
        Directory.CreateDirectory(cacheRoot);
        var installers = new List<string>();
        var selectedPacks = PublicModelPacks
            .Where(pack => enabledPackIds.Count == 0 || enabledPackIds.Contains(pack.PackId) || enabledPackIds.Contains(pack.RepositoryId) || enabledPackIds.Contains(pack.DisplayName))
            .ToArray();
        for (var index = 0; index < selectedPacks.Length; index++)
        {
            var definition = selectedPacks[index];
            var packRoot = Path.Combine(cacheRoot, definition.PackId);
            Directory.CreateDirectory(packRoot);
            EnsureSpaceForPublicPack(packRoot, definition);
            var setupPath = Path.Combine(packRoot, definition.InstallerFileName);
            if (!File.Exists(setupPath))
                File.Copy(Environment.ProcessPath!, setupPath, true);

            progress?.Report(new(90 + index, $"Fetching {definition.DisplayName}…"));
            var manifestPath = Path.Combine(packRoot, ModelPackApplication.ManifestName);
            DownloadFile(BuildHuggingFaceUrl(definition.RepositoryId, ModelPackApplication.ManifestName), manifestPath, $"Downloading {definition.DisplayName} manifest…", progress, 90 + index, 91 + index);

            var manifest = JsonSerializer.Deserialize<ModelPackManifest>(File.ReadAllText(manifestPath), JsonOptions.Default)
                           ?? throw new InvalidDataException($"The downloaded manifest for {definition.DisplayName} is invalid.");
            ModelPackEngine.ValidateManifest(manifest);

            var payloadPath = Path.Combine(packRoot, manifest.PayloadFile);
            if (!File.Exists(payloadPath) || !FileMatchesHash(payloadPath, manifest.Sha256))
            {
                DownloadFile(BuildHuggingFaceUrl(definition.RepositoryId, manifest.PayloadFile), payloadPath, $"Downloading {definition.DisplayName} payload…", progress, 91 + index, 92 + index);
                if (!FileMatchesHash(payloadPath, manifest.Sha256))
                    throw new InvalidDataException($"{definition.DisplayName} failed its integrity check after download.");
            }

            installers.Add(setupPath);
        }

        return installers;
    }

    private static void EnsureSpaceForPublicPack(string packRoot, PublicModelPackDefinition definition)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(packRoot)) ?? "C:\\";
        var drive = new DriveInfo(root);
        var required = (definition.PayloadBytes * 2) + (2L * 1024 * 1024 * 1024);
        if (drive.AvailableFreeSpace < required)
            throw new InvalidOperationException($"Not enough free space to install {definition.DisplayName}. Free up at least {PrerequisiteChecker.FormatBytes(required)} on {drive.Name}, then run Setup again.");
    }

    private static HashSet<string> LoadRequestedPublicPackIds()
    {
        var raw = Environment.GetEnvironmentVariable("SHADOWFRAME_PUBLIC_PACK_FILTER");
        if (string.IsNullOrWhiteSpace(raw)) return new(StringComparer.OrdinalIgnoreCase);
        return raw
            .Split(new[] { ',', ';', '|'}, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildHuggingFaceUrl(string repositoryId, string fileName)
    {
        var encodedSegments = fileName
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"https://huggingface.co/datasets/{repositoryId}/resolve/main/{string.Join("/", encodedSegments)}?download=true";
    }

    private static bool FileMatchesHash(string path, string expectedSha256)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void DownloadFile(string url, string destination, string label, IProgress<InstallProgress>? progress, int percentStart, int percentEnd)
    {
        var tempFile = destination + ".partial";
        if (File.Exists(tempFile))
            File.Delete(tempFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = DownloadClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        using var source = response.Content.ReadAsStream();
        var buffer = new byte[1024 * 1024];
        long completed = 0;
        int read;
        using (var target = File.Create(tempFile))
        {
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                completed += read;
                if (totalBytes is > 0)
                {
                    var ratio = Math.Clamp(completed / (double)totalBytes.Value, 0d, 1d);
                    var percent = percentStart + (int)Math.Round((percentEnd - percentStart) * ratio);
                    progress?.Report(new(percent, $"{label} {PrerequisiteChecker.FormatBytes(completed)} of {PrerequisiteChecker.FormatBytes(totalBytes.Value)}"));
                }
                else
                {
                    progress?.Report(new(percentStart, $"{label} {PrerequisiteChecker.FormatBytes(completed)} downloaded"));
                }
            }
            target.Flush(true);
        }
        ReplaceFileWithRetries(tempFile, destination);
    }

    private static void ReplaceFileWithRetries(string source, string destination)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);
                File.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(500 * (attempt + 1));
            }
        }

        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(source, destination);
    }

    public static int Uninstall(InstallOptions options)
    {
        var installRoot = Path.GetFullPath(options.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!options.Detached && Environment.ProcessPath is string processPath && processPath.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var detached = Path.Combine(Path.GetTempPath(), $"Shadowframe-Uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(processPath, detached, true);
            Process.Start(new ProcessStartInfo(detached, $"/UNINSTALL /DETACHED {(options.Silent ? "/SILENT " : "")}{(options.RemoveData ? "/REMOVEDATA " : "")}{(options.NoShortcuts ? "/NOSHORTCUTS " : "")}/INSTALLDIR=\"{installRoot}\"") { UseShellExecute = true });
            return 0;
        }

        if (!options.Silent)
        {
            var prompt = options.RemoveData
                ? "Remove Shadowframe AI and all local models, inputs, outputs, and generations?"
                : "Remove Shadowframe AI? Your models, inputs, outputs, and generations will be preserved.";
            if (MessageBox.Show(prompt, "Uninstall Shadowframe AI", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        }

        StopInstalledRuntime(installRoot);
        if (!options.NoShortcuts) RemoveShortcuts();
        Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false);
        if (Directory.Exists(installRoot)) DeleteWithRetries(installRoot);
        if (options.RemoveData)
        {
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shadowframe");
            if (Directory.Exists(dataRoot)) DeleteWithRetries(dataRoot);
        }
        if (!options.Silent)
        {
            var message = options.RemoveData
                ? "Shadowframe AI and its local model and generation data were removed."
                : "Shadowframe AI was removed. Your model and generation data was preserved.";
            MessageBox.Show(message, Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        if (options.Detached && Environment.ProcessPath is string detachedPath)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping 127.0.0.1 -n 3 >nul & del /f /q \"{detachedPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        return 0;
    }

    private static void RemoveShortcuts()
    {
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Shadowframe AI.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Shadowframe AI")
        })
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static void DeleteWithRetries(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(directory, true); return; }
            catch when (attempt < 4) { Thread.Sleep(750); }
        }
    }
}

internal sealed record PublicModelPackDefinition(string PackId, string DisplayName, string InstallerFileName, string RepositoryId, long PayloadBytes);

internal static class Shortcut
{
    public static void Create(string shortcutPath, string targetPath, string workingDirectory, string description, string arguments = "")
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        try
        {
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = description;
            shortcut.Arguments = arguments;
            shortcut.IconLocation = $"{targetPath},0";
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

internal sealed class InstallerForm : Form
{
    private readonly InstallOptions _options;
    private readonly PackageManifest _manifest;
    private readonly TextBox _installPath = new();
    private readonly TextBox _dataRoot = new();
    private readonly TextBox _outputRoot = new();
    private readonly Label _checks = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _primary = new();
    private readonly Button _cancel = new();
    private readonly Button _sfwPrompts = new();
    private readonly Button _nsfwPrompts = new();
    private readonly CheckBox _desktop = new();
    private readonly CheckBox _modelPacks = new();
    private readonly Label _singleRootHelp = new();
    private bool _installed;
    private readonly bool _publicRelease;

    public InstallerForm(InstallOptions options, PackageManifest manifest)
    {
        _options = options;
        _manifest = manifest;
        _publicRelease = Program.LoadReleaseProfile().Equals("public", StringComparison.OrdinalIgnoreCase);
        Text = "Shadowframe AI Setup";
        ClientSize = new Size(760, _publicRelease ? 608 : 680);
        MinimumSize = new Size(760, _publicRelease ? 608 : 680);
        BackColor = Color.FromArgb(10, 10, 11);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        var accent = Color.FromArgb(255, 102, 29);
        var title = new Label { Text = _publicRelease ? "Install Shadowframe AI" : "Install Shadowframe AI", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true, Location = new Point(42, 38) };
        var subtitle = new Label { Text = _publicRelease ? "One setup app. Local AI creation on your own GPU. Setup handles the required public packs automatically." : "Private local image and video generation, powered by your GPU.", ForeColor = Color.FromArgb(180, 185, 195), AutoSize = true, Location = new Point(46, 88) };
        var version = new Label { Text = _publicRelease ? $"PUBLIC CORE {manifest.Version}" : $"CORE {manifest.Version}", ForeColor = accent, Font = new Font("Segoe UI Semibold", 9), AutoSize = true, Location = new Point(47, 124) };

        _checks.Location = new Point(47, 156);
        _checks.Size = new Size(665, 92);
        _checks.ForeColor = Color.FromArgb(210, 213, 220);

        var pathLabel = new Label { Text = _publicRelease ? "Choose your Shadowframe folder" : "App install location", AutoSize = true, Location = new Point(47, 262) };
        _installPath.Text = options.InstallDirectory;
        _installPath.Location = new Point(47, 287);
        _installPath.Size = new Size(575, 30);
        _installPath.BackColor = Color.FromArgb(31, 31, 34);
        _installPath.ForeColor = Color.White;
        _installPath.BorderStyle = BorderStyle.FixedSingle;
        var browse = new Button { Text = "Browse", Location = new Point(632, 285), Size = new Size(80, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(29, 29, 31), ForeColor = Color.White };
        browse.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 69);
        browse.Click += (_, _) => Browse(_installPath, "Choose where Shadowframe AI will be installed", "Shadowframe AI");

        var dataLabel = new Label { Text = "Shadowframe library location", AutoSize = true, Location = new Point(47, 330) };
        _dataRoot.Text = options.DataRoot;
        _dataRoot.Location = new Point(47, 355);
        _dataRoot.Size = new Size(575, 30);
        _dataRoot.BackColor = Color.FromArgb(31, 31, 34);
        _dataRoot.ForeColor = Color.White;
        _dataRoot.BorderStyle = BorderStyle.FixedSingle;
        var browseData = new Button { Text = "Browse", Location = new Point(632, 353), Size = new Size(80, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(29, 29, 31), ForeColor = Color.White };
        browseData.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 69);
        browseData.Click += (_, _) => Browse(_dataRoot, "Choose where Shadowframe stores models and state", "Shadowframe");

        var outputLabel = new Label { Text = "Generation output location", AutoSize = true, Location = new Point(47, 398) };
        _outputRoot.Text = options.OutputRoot;
        _outputRoot.Location = new Point(47, 423);
        _outputRoot.Size = new Size(575, 30);
        _outputRoot.BackColor = Color.FromArgb(31, 31, 34);
        _outputRoot.ForeColor = Color.White;
        _outputRoot.BorderStyle = BorderStyle.FixedSingle;
        var browseOutput = new Button { Text = "Browse", Location = new Point(632, 421), Size = new Size(80, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(29, 29, 31), ForeColor = Color.White };
        browseOutput.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 69);
        browseOutput.Click += (_, _) => Browse(_outputRoot, "Choose where Shadowframe saves generated files", "");

        _singleRootHelp.Text = "Setup will install the app here, then automatically create your models, prompts, input, output, temp, and app-state folders inside this Shadowframe location.";
        _singleRootHelp.Location = new Point(47, 330);
        _singleRootHelp.Size = new Size(665, 56);
        _singleRootHelp.ForeColor = Color.FromArgb(180, 185, 195);
        _singleRootHelp.Visible = _publicRelease;

        _desktop.Text = "Create a Desktop shortcut";
        _desktop.Checked = options.DesktopShortcut;
        _desktop.AutoSize = true;
        _desktop.Location = new Point(47, _publicRelease ? 398 : 470);
        _desktop.ForeColor = Color.FromArgb(205, 208, 215);

        _modelPacks.Text = _publicRelease ? "Automatically download and install the required public model packs" : "Install adjacent Anima, Wan, and PhotoReal model packs automatically";
        _modelPacks.Checked = options.InstallModelPacks;
        _modelPacks.AutoSize = true;
        _modelPacks.Location = new Point(47, _publicRelease ? 428 : 500);
        _modelPacks.ForeColor = Color.FromArgb(205, 208, 215);

        _progress.Location = new Point(47, _publicRelease ? 463 : 535);
        _progress.Size = new Size(665, 10);
        _progress.Style = ProgressBarStyle.Continuous;
        _status.Text = _publicRelease ? "Ready — download one file, choose a folder, and let Setup handle the rest." : "Ready to install";
        _status.Location = new Point(47, _publicRelease ? 483 : 555);
        _status.Size = new Size(665, 28);
        _status.ForeColor = Color.FromArgb(168, 172, 182);

        _primary.Text = File.Exists(Path.Combine(options.InstallDirectory, "Shadowframe.exe")) ? "Repair / Update" : "Install";
        _primary.Location = new Point(522, _publicRelease ? 538 : 610);
        _primary.Size = new Size(190, 42);
        _primary.FlatStyle = FlatStyle.Flat;
        _primary.FlatAppearance.BorderSize = 0;
        _primary.BackColor = accent;
        _primary.ForeColor = Color.White;
        _primary.Font = new Font("Segoe UI Semibold", 10);
        _primary.Click += async (_, _) => await PrimaryAction();

        _cancel.Text = "Cancel";
        _cancel.Location = new Point(412, _publicRelease ? 538 : 610);
        _cancel.Size = new Size(100, 42);
        _cancel.FlatStyle = FlatStyle.Flat;
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 69);
        _cancel.BackColor = Color.FromArgb(26, 26, 29);
        _cancel.ForeColor = Color.White;
        _cancel.Click += (_, _) => Close();

        _sfwPrompts.Text = "Open SFW prompts";
        _sfwPrompts.Location = new Point(47, _publicRelease ? 538 : 610);
        _sfwPrompts.Size = new Size(150, 42);
        _sfwPrompts.FlatStyle = FlatStyle.Flat;
        _sfwPrompts.BackColor = Color.FromArgb(26, 26, 29);
        _sfwPrompts.ForeColor = Color.White;
        _sfwPrompts.Visible = false;
        _sfwPrompts.Click += (_, _) => OpenPromptFolder("SFW");

        _nsfwPrompts.Text = "Open NSFW prompts";
        _nsfwPrompts.Location = new Point(207, _publicRelease ? 538 : 610);
        _nsfwPrompts.Size = new Size(160, 42);
        _nsfwPrompts.FlatStyle = FlatStyle.Flat;
        _nsfwPrompts.BackColor = Color.FromArgb(26, 26, 29);
        _nsfwPrompts.ForeColor = Color.White;
        _nsfwPrompts.Visible = false;
        _nsfwPrompts.Click += (_, _) => OpenPromptFolder("NSFW");

        if (_publicRelease)
        {
            dataLabel.Visible = false;
            _dataRoot.Visible = false;
            browseData.Visible = false;
            outputLabel.Visible = false;
            _outputRoot.Visible = false;
            browseOutput.Visible = false;
            _installPath.TextChanged += (_, _) => SyncPublicRoots();
            SyncPublicRoots();
        }

        Controls.AddRange(new Control[] { title, subtitle, version, _checks, pathLabel, _installPath, browse, dataLabel, _dataRoot, browseData, outputLabel, _outputRoot, browseOutput, _singleRootHelp, _desktop, _modelPacks, _progress, _status, _primary, _cancel, _sfwPrompts, _nsfwPrompts });
        RefreshPrerequisites();
    }

    private void Browse(TextBox target, string description, string appendFolder)
    {
        using var dialog = new FolderBrowserDialog { InitialDirectory = target.Text, Description = description, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = string.IsNullOrWhiteSpace(appendFolder) ? dialog.SelectedPath : Path.Combine(dialog.SelectedPath, appendFolder);
            if (_publicRelease && ReferenceEquals(target, _installPath))
                SyncPublicRoots();
            RefreshPrerequisites();
        }
    }

    private void SyncPublicRoots()
    {
        if (!_publicRelease) return;
        var root = _installPath.Text;
        try { root = Path.GetFullPath(root); } catch { }
        _dataRoot.Text = root;
        _outputRoot.Text = root;
    }

    private void RefreshPrerequisites()
    {
        try
        {
        var results = PrerequisiteChecker.Run(new InstallOptions
        {
            InstallDirectory = Path.GetFullPath(_installPath.Text),
            DataRoot = Path.GetFullPath(_dataRoot.Text),
            OutputRoot = Path.GetFullPath(_outputRoot.Text),
            InstallModelPacks = _modelPacks.Checked
        }, _manifest);
            _checks.Text = results.DisplayText;
            _primary.Enabled = results.Blockers.Count == 0;
            if (results.Blockers.Count > 0) _status.Text = "Resolve the requirements above before installing.";
            else if (_publicRelease) _status.Text = "Ready — download one file, choose a folder, and let Setup handle the rest.";
        }
        catch (Exception exception)
        {
            _checks.Text = $"× {exception.Message}";
            _primary.Enabled = false;
        }
    }

    private async Task PrimaryAction()
    {
        if (_installed)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_installPath.Text, "Shadowframe.exe")) { UseShellExecute = true });
            Close();
            return;
        }

        _primary.Enabled = _cancel.Enabled = _installPath.Enabled = _dataRoot.Enabled = _outputRoot.Enabled = _desktop.Enabled = _modelPacks.Enabled = false;
        var progress = new Progress<InstallProgress>(update => { _progress.Value = Math.Clamp(update.Percent, 0, 100); _status.Text = update.Message; });
        try
        {
            var options = new InstallOptions
            {
                InstallDirectory = Path.GetFullPath(_installPath.Text),
                DataRoot = Path.GetFullPath(_dataRoot.Text),
                OutputRoot = Path.GetFullPath(_outputRoot.Text),
                DesktopShortcut = _desktop.Checked,
                InstallModelPacks = _modelPacks.Checked
            };
            await Task.Run(() => InstallerEngine.Install(options, _manifest, progress));
            _installed = true;
            _status.Text = _publicRelease ? "Check out the included public sample prompts here." : "Check out our sample prompts here.";
            _primary.Text = "Launch Shadowframe";
            _primary.Enabled = true;
            _cancel.Text = "Close";
            _cancel.Enabled = true;
            _sfwPrompts.Visible = Directory.Exists(PromptFolder("SFW"));
            _nsfwPrompts.Visible = !_publicRelease && Directory.Exists(PromptFolder("NSFW"));
            if (_sfwPrompts.Visible && !_nsfwPrompts.Visible)
            {
                _sfwPrompts.Text = _publicRelease ? "Open included prompts" : "Open sample prompts";
            }
            else
            {
                _sfwPrompts.Text = "Open SFW prompts";
            }
        }
        catch (Exception exception)
        {
            _status.Text = "Installation failed.";
            MessageBox.Show(this, exception.Message, "Shadowframe AI Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _primary.Enabled = _cancel.Enabled = _installPath.Enabled = _dataRoot.Enabled = _outputRoot.Enabled = _desktop.Enabled = _modelPacks.Enabled = true;
        }
    }

    private string PromptFolder(string category) => Path.Combine(Path.GetFullPath(_dataRoot.Text), "Sample Prompts", category);

    private void OpenPromptFolder(string category)
    {
        var folder = PromptFolder(category);
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}

