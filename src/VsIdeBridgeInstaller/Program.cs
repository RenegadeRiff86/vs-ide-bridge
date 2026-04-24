using System.Diagnostics;
using System.Security.Principal;

namespace VsIdeBridgeInstaller;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ERROR: installer is Windows-only.");
            return 1;
        }

        if (args.Length > 0 && IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var (verb, optionArgs) = ResolveVerb(args);
        Dictionary<string, string?> options = ParseOptions(optionArgs);

        try
        {
            if (!HasFlag(options, "skip-admin-check"))
            {
                EnsureAdmin();
            }

            return verb switch
            {
                "install" => RunInstall(options),
                "uninstall" => RunUninstall(options),
                _ => Fail($"Unknown command '{verb}'.")
            };
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static (string Verb, string[] OptionArgs) ResolveVerb(string[] args)
    {
        if (args.Length == 0)
        {
            return ("install", []);
        }

        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            // Allow running as: installer.exe --configuration Release
            return ("install", args);
        }

        return (args[0].Trim().ToLowerInvariant(), args.Skip(1).ToArray());
    }

    private static int RunInstall(Dictionary<string, string?> options)
    {
        string? repoRoot = GetPathOption(options, "repo-root") ?? FindRepoRoot();
        string configuration = GetOption(options, "configuration") ?? InstallerDefaults.DefaultConfiguration;
        string installDir = GetPathOption(options, "install-dir") ?? InstallerDefaults.DefaultInstallDir;
        string serviceName = GetOption(options, "service-name") ?? InstallerDefaults.ServiceName;
        string vsixId = GetOption(options, "vsix-id") ?? InstallerDefaults.VsixId;
        int idleSoftSeconds = GetIntOption(options, "idle-soft-seconds", InstallerDefaults.DefaultIdleSoftSeconds);
        int idleHardSeconds = GetIntOption(options, "idle-hard-seconds", InstallerDefaults.DefaultIdleHardSeconds);

        string serviceSource = GetPathOption(options, "service-source")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridgeService", "bin", configuration, "net8.0-windows");
        string launcherSource = GetPathOption(options, "launcher-source")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridgeLauncher", "bin", configuration);
        string vsixPath = GetPathOption(options, "vsix-path")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridge", "bin", configuration, "net472", InstallerDefaults.VsixFileName);

        bool skipVsix = HasFlag(options, "skip-vsix");
        bool skipService = HasFlag(options, "skip-service");

        Directory.CreateDirectory(installDir);

        if (!skipService)
        {
            if (!Directory.Exists(serviceSource))
            {
                return Fail($"Service source directory not found: {serviceSource}");
            }

            if (!Directory.Exists(launcherSource))
            {
                return Fail($"Launcher source directory not found: {launcherSource}");
            }

            string serviceDest = Path.Combine(installDir, InstallerDefaults.ServiceDirectoryName);
            string legacyCliDest = Path.Combine(installDir, "cli");
            if (Directory.Exists(legacyCliDest))
            {
                Directory.Delete(legacyCliDest, recursive: true);
                Console.WriteLine($"Removed legacy CLI directory -> {legacyCliDest}");
            }

            CopyDirectory(serviceSource, serviceDest);
            CopyDirectory(launcherSource, serviceDest);
            string installedServiceExe = Path.Combine(serviceDest, InstallerDefaults.ServiceExecutableName);
            string installedLauncherExe = Path.Combine(serviceDest, InstallerDefaults.LauncherExecutableName);
            if (!File.Exists(installedServiceExe))
            {
                return Fail($"Service executable not found after copy: {installedServiceExe}");
            }

            if (!File.Exists(installedLauncherExe))
            {
                return Fail($"Launcher executable not found after copy: {installedLauncherExe}");
            }

            InstallOrUpdateService(serviceName, installedServiceExe, idleSoftSeconds, idleHardSeconds);
            Console.WriteLine($"Service '{serviceName}' installed (StartType=Automatic).");
        }

        if (!skipVsix)
        {
            if (!File.Exists(vsixPath))
            {
                return Fail($"VSIX not found: {vsixPath}");
            }

            UninstallVsix(vsixId);
            UninstallVsix(InstallerDefaults.LegacyVsixId);
            InstallVsix(vsixPath);
            Console.WriteLine($"VSIX installed/updated ({vsixId}).");
        }

        Console.WriteLine("Install complete.");
        return 0;
    }

    private static int RunUninstall(Dictionary<string, string?> options)
    {
        string installDir = GetPathOption(options, "install-dir") ?? InstallerDefaults.DefaultInstallDir;
        string serviceName = GetOption(options, "service-name") ?? InstallerDefaults.ServiceName;
        string vsixId = GetOption(options, "vsix-id") ?? InstallerDefaults.VsixId;
        bool skipVsix = HasFlag(options, "skip-vsix");
        bool skipService = HasFlag(options, "skip-service");

        if (!skipService)
        {
            RemoveService(serviceName);
            Console.WriteLine($"Service '{serviceName}' removed if it existed.");
        }

        if (!skipVsix)
        {
            UninstallVsix(vsixId);
            UninstallVsix(InstallerDefaults.LegacyVsixId);
            Console.WriteLine($"VSIX uninstall attempted ({vsixId}).");
        }

        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, recursive: true);
            Console.WriteLine($"Removed install directory: {installDir}");
        }

        Console.WriteLine("Uninstall complete.");
        return 0;
    }

    private static void InstallVsix(string vsixPath)
    {
        string installer = FindVsixInstallerPath();
        int exitCode = RunProcess(installer, $"/quiet \"{vsixPath}\"");
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"VSIX install failed with exit code {exitCode}.");
        }
    }

    private static void UninstallVsix(string vsixId)
    {
        string installer = FindVsixInstallerPath();
        int exitCode = RunProcess(installer, $"/quiet /uninstall:{vsixId}");
        if (exitCode != 0)
        {
            Console.WriteLine($"VSIX uninstall returned exit code {exitCode}. Continuing.");
        }
    }

    private static void InstallOrUpdateService(string serviceName, string serviceExePath, int idleSoftSeconds, int idleHardSeconds)
    {
        RemoveService(serviceName);
        string binPath = $"\"{serviceExePath}\" --idle-soft-seconds {idleSoftSeconds} --idle-hard-seconds {idleHardSeconds}";
        string createArgs = $"create \"{serviceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{InstallerDefaults.ServiceDisplayName}\"";
        int createExit = RunProcess("sc.exe", createArgs);
        if (createExit != 0)
        {
            throw new InvalidOperationException($"Failed to create service '{serviceName}'. Exit code: {createExit}");
        }

        _ = RunProcess("sc.exe", $"description \"{serviceName}\" \"{InstallerDefaults.ServiceDescription}\"");
    }

    private static void RemoveService(string serviceName)
    {
        _ = RunProcess("sc.exe", $"stop \"{serviceName}\"");
        _ = RunProcess("sc.exe", $"delete \"{serviceName}\"");
    }

    private static string FindVsixInstallerPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string root = Path.Combine(programFiles, "Microsoft Visual Studio", InstallerDefaults.VisualStudioMajorVersion);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"Visual Studio {InstallerDefaults.VisualStudioMajorVersion} installation path not found.");
        }

        foreach (string edition in InstallerDefaults.VisualStudioEditions)
        {
            string candidate = Path.Combine(root, edition, "Common7", "IDE", "VSIXInstaller.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? fallback = Directory.GetFiles(root, "VSIXInstaller.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        throw new FileNotFoundException($"VSIXInstaller.exe not found under Visual Studio {InstallerDefaults.VisualStudioMajorVersion} install path.");
    }

    private static int RunProcess(string fileName, string arguments)
    {
        using Process installerProcess = new()
        {
            StartInfo = new()
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        installerProcess.Start();
        string stdout = installerProcess.StandardOutput.ReadToEnd();
        string stderr = installerProcess.StandardError.ReadToEnd();
        installerProcess.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr.Trim());
        }

        return installerProcess.ExitCode;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destinationDir, fileName), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            string childName = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(destinationDir, childName));
        }
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        DirectoryInfo? directory = new(current);
        while (directory is not null)
        {
            string sln = Path.Combine(directory.FullName, InstallerDefaults.SolutionFileName);
            if (File.Exists(sln))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not infer repo root. Pass --repo-root <path>.");
    }

    private static void EnsureAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Run this installer from an elevated terminal (Administrator).");
        }
    }

    private static bool IsHelp(string token) => token is "-h" or "--help" or "/?";

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        Dictionary<string, string?> map = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument: {arg}");
            }

            string key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[++i];
            }
            else
            {
                map[key] = null;
            }
        }

        return map;
    }

    private static string? GetOption(Dictionary<string, string?> options, string key)
    {
        return options.TryGetValue(key, out string? value) ? value : null;
    }

    private static string? GetPathOption(Dictionary<string, string?> options, string key)
    {
        string? value = GetOption(options, key);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }

    private static int GetIntOption(Dictionary<string, string?> options, string key, int defaultValue)
    {
        string? raw = GetOption(options, key);
        return int.TryParse(raw, out int value) && value > 0 ? value : defaultValue;
    }

    private static bool HasFlag(Dictionary<string, string?> options, string key)
    {
        return options.ContainsKey(key);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(InstallerDefaults.InstallerExecutableName);
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  installer.exe                  # default install");
        Console.WriteLine("  installer.exe install [options]");
        Console.WriteLine("  installer.exe uninstall [options]");
        Console.WriteLine();
        Console.WriteLine("Install options:");
        Console.WriteLine($"  --configuration <cfg>      Build config (default: {InstallerDefaults.DefaultConfiguration})");
        Console.WriteLine($"  --install-dir <path>       Install root (default: {InstallerDefaults.DefaultInstallDir})");
        Console.WriteLine("  --repo-root <path>         Repo root override");
        Console.WriteLine("  --service-source <path>    Service source folder override");
        Console.WriteLine($"  --service-name <name>      Service name (default: {InstallerDefaults.ServiceName})");
        Console.WriteLine($"  --idle-soft-seconds <n>    Idle drain start (default: {InstallerDefaults.DefaultIdleSoftSeconds})");
        Console.WriteLine($"  --idle-hard-seconds <n>    Idle stop timeout (default: {InstallerDefaults.DefaultIdleHardSeconds})");
        Console.WriteLine("  --vsix-path <path>         VSIX override path");
        Console.WriteLine($"  --vsix-id <id>             VSIX id (default: {InstallerDefaults.VsixId})");
        Console.WriteLine("  --skip-service             Do not install service");
        Console.WriteLine("  --skip-vsix                Do not install VSIX");
        Console.WriteLine("  --skip-admin-check         Bypass elevation check (automation only)");
        Console.WriteLine();
        Console.WriteLine("Uninstall options:");
        Console.WriteLine("  --install-dir <path>       Install root to remove");
        Console.WriteLine("  --service-name <name>      Service name to remove");
        Console.WriteLine("  --vsix-id <id>             VSIX id to uninstall");
        Console.WriteLine("  --skip-service             Do not remove service");
        Console.WriteLine("  --skip-vsix                Do not uninstall VSIX");
        Console.WriteLine("  --skip-admin-check         Bypass elevation check (automation only)");
    }
}
