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
        var options = ParseOptions(optionArgs);

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
        catch (Exception ex)
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
        var repoRoot = GetPathOption(options, "repo-root") ?? FindRepoRoot();
        var configuration = GetOption(options, "configuration") ?? InstallerDefaults.DefaultConfiguration;
        var installDir = GetPathOption(options, "install-dir") ?? InstallerDefaults.DefaultInstallDir;
        var serviceName = GetOption(options, "service-name") ?? InstallerDefaults.ServiceName;
        var vsixId = GetOption(options, "vsix-id") ?? InstallerDefaults.VsixId;
        var idleSoftSeconds = GetIntOption(options, "idle-soft-seconds", InstallerDefaults.DefaultIdleSoftSeconds);
        var idleHardSeconds = GetIntOption(options, "idle-hard-seconds", InstallerDefaults.DefaultIdleHardSeconds);

        var cliSource = GetPathOption(options, "cli-source")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridgeService", "bin", configuration, "cli", "net8.0");
        var serviceSource = GetPathOption(options, "service-source")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridgeService", "bin", configuration, "net8.0-windows");
        var launcherSource = GetPathOption(options, "launcher-source")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridgeLauncher", "bin", configuration);
        var vsixPath = GetPathOption(options, "vsix-path")
            ?? Path.Combine(repoRoot, "src", "VsIdeBridge", "bin", configuration, "net472", InstallerDefaults.VsixFileName);

        var skipVsix = HasFlag(options, "skip-vsix");
        var skipService = HasFlag(options, "skip-service");

        if (!Directory.Exists(cliSource))
        {
            return Fail($"CLI source directory not found: {cliSource}");
        }

        Directory.CreateDirectory(installDir);

        var cliDest = Path.Combine(installDir, InstallerDefaults.CliDirectoryName);
        CopyDirectory(cliSource, cliDest);
        Console.WriteLine($"Installed CLI files -> {cliDest}");

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

            var serviceDest = Path.Combine(installDir, InstallerDefaults.ServiceDirectoryName);
            CopyDirectory(serviceSource, serviceDest);
            CopyDirectory(launcherSource, serviceDest);
            var installedServiceExe = Path.Combine(serviceDest, InstallerDefaults.ServiceExecutableName);
            var installedLauncherExe = Path.Combine(serviceDest, InstallerDefaults.LauncherExecutableName);
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

            UninstallVsix(InstallerDefaults.LegacyVsixId);
            InstallVsix(vsixPath);
            Console.WriteLine($"VSIX installed/updated ({vsixId}).");
        }

        Console.WriteLine("Install complete.");
        return 0;
    }

    private static int RunUninstall(Dictionary<string, string?> options)
    {
        var installDir = GetPathOption(options, "install-dir") ?? InstallerDefaults.DefaultInstallDir;
        var serviceName = GetOption(options, "service-name") ?? InstallerDefaults.ServiceName;
        var vsixId = GetOption(options, "vsix-id") ?? InstallerDefaults.VsixId;
        var skipVsix = HasFlag(options, "skip-vsix");
        var skipService = HasFlag(options, "skip-service");

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
        var installer = FindVsixInstallerPath();
        var exitCode = RunProcess(installer, $"/quiet \"{vsixPath}\"");
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"VSIX install failed with exit code {exitCode}.");
        }
    }

    private static void UninstallVsix(string vsixId)
    {
        var installer = FindVsixInstallerPath();
        var exitCode = RunProcess(installer, $"/quiet /uninstall:{vsixId}");
        if (exitCode != 0)
        {
            Console.WriteLine($"VSIX uninstall returned exit code {exitCode}. Continuing.");
        }
    }

    private static void InstallOrUpdateService(string serviceName, string serviceExePath, int idleSoftSeconds, int idleHardSeconds)
    {
        RemoveService(serviceName);
        var binPath = $"\"{serviceExePath}\" --idle-soft-seconds {idleSoftSeconds} --idle-hard-seconds {idleHardSeconds}";
        var createArgs = $"create \"{serviceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{InstallerDefaults.ServiceDisplayName}\"";
        var createExit = RunProcess("sc.exe", createArgs);
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
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.Combine(programFiles, "Microsoft Visual Studio", InstallerDefaults.VisualStudioMajorVersion);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"Visual Studio {InstallerDefaults.VisualStudioMajorVersion} installation path not found.");
        }

        foreach (var edition in InstallerDefaults.VisualStudioEditions)
        {
            var candidate = Path.Combine(root, edition, "Common7", "IDE", "VSIXInstaller.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fallback = Directory.GetFiles(root, "VSIXInstaller.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        throw new FileNotFoundException($"VSIXInstaller.exe not found under Visual Studio {InstallerDefaults.VisualStudioMajorVersion} install path.");
    }

    private static int RunProcess(string fileName, string arguments)
    {
        using var installerProcess = new Process
        {
            StartInfo = new ProcessStartInfo
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
        var stdout = installerProcess.StandardOutput.ReadToEnd();
        var stderr = installerProcess.StandardError.ReadToEnd();
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

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destinationDir, fileName), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var childName = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(destinationDir, childName));
        }
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        var directory = new DirectoryInfo(current);
        while (directory is not null)
        {
            var sln = Path.Combine(directory.FullName, InstallerDefaults.SolutionFileName);
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
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Run this installer from an elevated terminal (Administrator).");
        }
    }

    private static bool IsHelp(string token) => token is "-h" or "--help" or "/?";

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument: {arg}");
            }

            var key = arg[2..];
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
        return options.TryGetValue(key, out var value) ? value : null;
    }

    private static string? GetPathOption(Dictionary<string, string?> options, string key)
    {
        var value = GetOption(options, key);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }

    private static int GetIntOption(Dictionary<string, string?> options, string key, int defaultValue)
    {
        var raw = GetOption(options, key);
        return int.TryParse(raw, out var value) && value > 0 ? value : defaultValue;
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
        Console.WriteLine("  --cli-source <path>        CLI source folder override");
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
