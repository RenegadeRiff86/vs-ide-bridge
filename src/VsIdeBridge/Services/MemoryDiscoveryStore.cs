using System;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Services;

internal sealed class MemoryDiscoveryStore
{
    private const string MapName = @"Global\VsIdeBridge.Discovery.v1";
    private const string MutexName = @"Global\VsIdeBridge.Discovery.v1.mutex";
    private const int CapacityBytes = 1024 * 1024;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(6);
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public void Upsert(object discoveryRecord)
    {
        var item = JObject.FromObject(discoveryRecord);
        item["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");

        ExecuteWithStore(root =>
        {
            var items = GetItems(root);
            PurgeExpired(items);

            var instanceId = item["instanceId"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                var existing = items
                    .OfType<JObject>()
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate["instanceId"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    existing.Replace(item);
                }
                else
                {
                    items.Add(item);
                }
            }
        });
    }

    public void Remove(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        ExecuteWithStore(root =>
        {
            var items = GetItems(root);
            var stale = items
                .OfType<JObject>()
                .Where(candidate =>
                    string.Equals(candidate["instanceId"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var item in stale)
            {
                item.Remove();
            }
        });
    }

    private static JArray GetItems(JObject root)
    {
        var items = root["items"] as JArray;
        if (items is not null)
        {
            return items;
        }

        items = new JArray();
        root["items"] = items;
        return items;
    }

    private static void PurgeExpired(JArray items)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(EntryTtl);
        var staleItems = items
            .OfType<JObject>()
            .Where(item =>
            {
                var updatedAtUtc = item["updatedAtUtc"]?.ToString();
                if (string.IsNullOrWhiteSpace(updatedAtUtc))
                {
                    return true;
                }

                return !DateTimeOffset.TryParse(updatedAtUtc, out var parsed) || parsed < cutoff;
            })
            .ToArray();

        foreach (var stale in staleItems)
        {
            stale.Remove();
        }
    }

    private static void ExecuteWithStore(Action<JObject> mutate)
    {
        using var mutex = new System.Threading.Mutex(false, MutexName);
        var hasLock = false;
        try
        {
            hasLock = mutex.WaitOne(TimeSpan.FromSeconds(2));
            if (!hasLock)
            {
                return;
            }

            using var map = MemoryMappedFile.CreateOrOpen(MapName, CapacityBytes, MemoryMappedFileAccess.ReadWrite);
            using var view = map.CreateViewStream(0, CapacityBytes, MemoryMappedFileAccess.ReadWrite);
            var root = ReadRoot(view);
            mutate(root);
            WriteRoot(view, root);
        }
        catch
        {
            // Best-effort store. Discovery JSON remains the compatibility fallback.
        }
        finally
        {
            if (hasLock)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static JObject ReadRoot(MemoryMappedViewStream view)
    {
        view.Position = 0;
        var lenBuffer = new byte[4];
        var bytesRead = view.Read(lenBuffer, 0, lenBuffer.Length);
        if (bytesRead < lenBuffer.Length)
        {
            return new JObject { ["items"] = new JArray() };
        }

        var payloadLength = BitConverter.ToInt32(lenBuffer, 0);
        if (payloadLength <= 0 || payloadLength > CapacityBytes - 4)
        {
            return new JObject { ["items"] = new JArray() };
        }

        var payload = new byte[payloadLength];
        bytesRead = view.Read(payload, 0, payload.Length);
        if (bytesRead != payloadLength)
        {
            return new JObject { ["items"] = new JArray() };
        }

        try
        {
            return JObject.Parse(Utf8NoBom.GetString(payload));
        }
        catch
        {
            return new JObject { ["items"] = new JArray() };
        }
    }

    private static void WriteRoot(MemoryMappedViewStream view, JObject root)
    {
        root["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");

        var payload = Utf8NoBom.GetBytes(root.ToString());
        if (payload.Length > CapacityBytes - 4)
        {
            // Keep dropping oldest entries until the payload fits.
            var items = GetItems(root)
                .OfType<JObject>()
                .OrderBy(item => item["updatedAtUtc"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in items)
            {
                item.Remove();
                payload = Utf8NoBom.GetBytes(root.ToString());
                if (payload.Length <= CapacityBytes - 4)
                {
                    break;
                }
            }
        }

        if (payload.Length > CapacityBytes - 4)
        {
            return;
        }

        view.Position = 0;
        var length = BitConverter.GetBytes(payload.Length);
        view.Write(length, 0, length.Length);
        view.Write(payload, 0, payload.Length);

        var remaining = CapacityBytes - 4 - payload.Length;
        if (remaining > 0)
        {
            var zeros = new byte[Math.Min(remaining, 4096)];
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, zeros.Length);
                view.Write(zeros, 0, chunk);
                remaining -= chunk;
            }
        }

        view.Flush();
    }
}
