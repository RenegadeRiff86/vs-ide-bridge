using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// Discovers live VS IDE Bridge instances from memory-mapped stores and JSON pipe files.
internal static class VsDiscovery
{
    private const string LocalMapName = @"Local\VsIdeBridge.Discovery.v1";
    private const string LocalMutexName = @"Local\VsIdeBridge.Discovery.v1.mutex";
    private const string GlobalMapName = @"Global\VsIdeBridge.Discovery.v1";
    private const string GlobalMutexName = @"Global\VsIdeBridge.Discovery.v1.mutex";
    private const int MemoryCapacityBytes = 1024 * 1024;

    private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(2);
    private static readonly (string Map, string Mutex, string Source)[] MemoryStores =
    [
        (LocalMapName,  LocalMutexName,  "memory://local/VsIdeBridge.Discovery.v1"),
        (GlobalMapName, GlobalMutexName, "memory://global/VsIdeBridge.Discovery.v1"),
    ];

    public static async Task<IReadOnlyList<BridgeInstance>> ListAsync(DiscoveryMode mode = DiscoveryMode.MemoryFirst)
    {
        IReadOnlyList<BridgeInstance> jsonInstances = mode == DiscoveryMode.MemoryFirst
            || mode == DiscoveryMode.JsonOnly
            || mode == DiscoveryMode.Hybrid
            ? await ListJsonAsync().ConfigureAwait(false)
            : [];

        if (mode == DiscoveryMode.JsonOnly) return jsonInstances;

        List<BridgeInstance> memoryInstances = ListMemory();
        return mode == DiscoveryMode.MemoryFirst
            ? Merge(memoryInstances, jsonInstances, preferPrimary: true)
            : Merge(memoryInstances, jsonInstances, preferPrimary: false);
    }

    public static async Task<BridgeInstance> SelectAsync(BridgeInstanceSelector selector, DiscoveryMode mode = DiscoveryMode.MemoryFirst)
    {
        IReadOnlyList<BridgeInstance> instances = await ListAsync(mode).ConfigureAwait(false);

        if (instances.Count == 0)
            throw new BridgeException(
                "No live VS IDE Bridge instance found. Open Visual Studio with the VS IDE Bridge extension installed.");

        BridgeInstance[] matches = [.. Filter(instances, selector)];
        if (matches.Length == 1) return matches[0];

        if (matches.Length == 0)
            throw new BridgeException(selector.HasAny
                ? $"No live VS IDE Bridge instance matched {selector.Describe()}. Call list_instances to see available instances."
                : "Multiple VS IDE Bridge instances are available. Call bind_solution or bind_instance to select one.");

        throw new BridgeException(
            $"Multiple VS IDE Bridge instances matched {selector.Describe()}. Call bind_instance with a specific instance_id.");
    }

    public static IEnumerable<BridgeInstance> Filter(IEnumerable<BridgeInstance> instances, BridgeInstanceSelector sel)
    {
        foreach (BridgeInstance inst in instances)
        {
            if (!string.IsNullOrWhiteSpace(sel.InstanceId) &&
                !string.Equals(inst.InstanceId, sel.InstanceId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (sel.ProcessId.HasValue && inst.ProcessId != sel.ProcessId.Value)
                continue;

            if (!string.IsNullOrWhiteSpace(sel.PipeName) &&
                !string.Equals(inst.PipeName, sel.PipeName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(sel.SolutionHint))
            {
                bool matched = inst.SolutionPath.Contains(sel.SolutionHint, StringComparison.OrdinalIgnoreCase)
                    || inst.SolutionName.Contains(sel.SolutionHint, StringComparison.OrdinalIgnoreCase);
                if (!matched) continue;
            }

            yield return inst;
        }
    }

    // ── Memory-mapped store discovery ──────────────────────────────────────────

    private static List<BridgeInstance> ListMemory()
    {
        if (!OperatingSystem.IsWindows()) return [];

        List<BridgeInstance> collected = [];
        foreach ((string mapName, string mutexName, string source) in MemoryStores)
        {
            List<BridgeInstance> fromStore = TryListMemoryStore(mapName, mutexName, source);
            collected.AddRange(fromStore);
        }

        if (collected.Count <= 1) return collected;

        return [.. collected
            .GroupBy(i => $"{i.InstanceId}:{i.ProcessId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.LastWriteTimeUtc).First())
            .OrderByDescending(i => i.LastWriteTimeUtc)];
    }

    private static List<BridgeInstance> TryListMemoryStore(string mapName, string mutexName, string source)
    {
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            using System.Threading.Mutex mutex = new(false, mutexName);
            bool hasLock = false;
            try
            {
                hasLock = mutex.WaitOne(MutexWait);
                if (!hasLock) return [];

                using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(mapName);
                using MemoryMappedViewStream stream = mmf.CreateViewStream(0, MemoryCapacityBytes, MemoryMappedFileAccess.Read);
                byte[] raw = new byte[MemoryCapacityBytes];
                int read = stream.Read(raw, 0, raw.Length);
                int nullPos = Array.IndexOf(raw, (byte)0);
                int len = nullPos >= 0 ? nullPos : read;
                string json = Encoding.UTF8.GetString(raw, 0, len).Trim();
                if (string.IsNullOrWhiteSpace(json)) return [];

                return ParseInstancesFromJson(json, source);
            }
            finally
            {
                if (hasLock) mutex.ReleaseMutex();
            }
        }
        catch
        {
            return [];
        }
    }

    // ── JSON pipe-file discovery ───────────────────────────────────────────────

    private static async Task<IReadOnlyList<BridgeInstance>> ListJsonAsync()
    {
        IEnumerable<string> tempDirs = new[]
            {
                Environment.GetEnvironmentVariable("TEMP"),
                Environment.GetEnvironmentVariable("TMP"),
                Path.GetTempPath(),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string tempDir in tempDirs)
        {
            string directory = Path.Combine(tempDir, "vs-ide-bridge", "pipes");
            try
            {
                if (!Directory.Exists(directory)) continue;

                FileInfo[] files = [.. Directory.GetFiles(directory, "bridge-*.json")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)];

                if (files.Length == 0) continue;

                List<BridgeInstance> instances = [];
                foreach (FileInfo file in files)
                {
                    BridgeInstance? inst = await TryLoadJsonFileAsync(file.FullName).ConfigureAwait(false);
                    if (inst is not null) instances.Add(inst);
                }

                if (instances.Count > 0) return instances;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return [];
    }

    private static async Task<BridgeInstance?> TryLoadJsonFileAsync(string path)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            JsonObject? obj = JsonNode.Parse(json) as JsonObject;
            if (obj is null) return null;
            return ParseInstanceFromObject(obj, path);
        }
        catch
        {
            return null;
        }
    }

    // ── Parsing helpers ────────────────────────────────────────────────────────

    private static List<BridgeInstance> ParseInstancesFromJson(string json, string source)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            List<BridgeInstance> result = [];
            if (node is JsonArray arr)
            {
                foreach (JsonNode? item in arr)
                {
                    if (item is JsonObject obj)
                    {
                        BridgeInstance? inst = ParseInstanceFromObject(obj, source);
                        if (inst is not null) result.Add(inst);
                    }
                }
            }
            else if (node is JsonObject single)
            {
                BridgeInstance? inst = ParseInstanceFromObject(single, source);
                if (inst is not null) result.Add(inst);
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static BridgeInstance? ParseInstanceFromObject(JsonObject obj, string source)
    {
        string? instanceId = obj["instanceId"]?.GetValue<string>();
        string? pipeName = obj["pipeName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(pipeName))
            return null;

        return new BridgeInstance
        {
            InstanceId = instanceId,
            PipeName = pipeName,
            ProcessId = obj["pid"]?.GetValue<int>() ?? 0,
            SolutionPath = obj["solutionPath"]?.GetValue<string>() ?? string.Empty,
            SolutionName = Path.GetFileName(obj["solutionPath"]?.GetValue<string>() ?? string.Empty),
            Source = source,
            StartedAtUtc = obj["startedAtUtc"]?.GetValue<string>(),
            DiscoveryFile = source,
            LastWriteTimeUtc = DateTime.TryParse(
                obj["lastWriteTimeUtc"]?.GetValue<string>(), out DateTime dt) ? dt : DateTime.MinValue,
        };
    }

    // ── Merge logic ────────────────────────────────────────────────────────────

    private static List<BridgeInstance> Merge(
        List<BridgeInstance> primary,
        IReadOnlyList<BridgeInstance> secondary,
        bool preferPrimary)
    {
        if (primary.Count == 0) return [.. secondary];
        if (secondary.Count == 0) return primary;

        System.Collections.Generic.HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<BridgeInstance> result = [];

        IEnumerable<BridgeInstance> first = preferPrimary ? primary : (IEnumerable<BridgeInstance>)secondary;
        IEnumerable<BridgeInstance> second = preferPrimary ? secondary : (IEnumerable<BridgeInstance>)primary;

        foreach (BridgeInstance inst in first)
        {
            if (seen.Add(inst.PipeName)) result.Add(inst);
        }

        foreach (BridgeInstance inst in second)
        {
            if (seen.Add(inst.PipeName)) result.Add(inst);
        }

        return result;
    }
}
