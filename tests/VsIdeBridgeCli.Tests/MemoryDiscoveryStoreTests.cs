using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Services;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public class MemoryDiscoveryStoreTests
{
    [Fact]
    public void Upsert_DoesNotThrow_WhenMutexFactoryThrows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new MemoryDiscoveryStore(
            mapName: "VsIdeBridge.Tests.NoopMap",
            mutexName: "VsIdeBridge.Tests.NoopMap.mutex",
            capacityBytes: 4096,
            lockTimeout: TimeSpan.FromMilliseconds(25),
            mutexFactory: static _ => throw new UnauthorizedAccessException("simulated"),
            mapFactory: static (_, _) => throw new InvalidOperationException("map not used"));

        var exception = Record.Exception(() => store.Upsert(new
        {
            instanceId = "test-instance",
            pipeName = "test-pipe",
            processId = 1,
            solutionPath = "",
            solutionName = "",
        }));

        Assert.Null(exception);
    }

    [Fact]
    public void Upsert_ThenRemove_RoundTripsExpectedInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var mapName = $"VsIdeBridge.Tests.{suffix}";
        var mutexName = $"{mapName}.mutex";
        const int capacityBytes = 64 * 1024;
        var instanceId = $"instance-{suffix}";
        var mapFilePath = Path.Combine(Path.GetTempPath(), $"vs-ide-bridge-store-{suffix}.bin");
        var store = CreateStore(
            mapName,
            mutexName,
            capacityBytes,
            TimeSpan.FromMilliseconds(100),
            mapFilePath);

        try
        {
            store.Upsert(new
            {
                instanceId,
                pipeName = "pipe-1",
                processId = 123,
                solutionPath = @"C:\repo\test.sln",
                solutionName = "test",
            });

            var rootAfterUpsert = ReadRootFromFile(mapFilePath, capacityBytes);
            Assert.NotNull(rootAfterUpsert);
            Assert.Contains(GetItems(rootAfterUpsert!), item =>
                string.Equals(item["instanceId"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase));

            store.Remove(instanceId);

            var rootAfterRemove = ReadRootFromFile(mapFilePath, capacityBytes);
            Assert.NotNull(rootAfterRemove);
            Assert.DoesNotContain(GetItems(rootAfterRemove!), item =>
                string.Equals(item["instanceId"]?.ToString(), instanceId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(mapFilePath))
            {
                File.Delete(mapFilePath);
            }
        }
    }

    [Fact]
    public void Upsert_ReturnsQuickly_WhenMutexIsHeldByAnotherThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var mapName = $"VsIdeBridge.Tests.Locked.{suffix}";
        var mutexName = $"{mapName}.mutex";
        const int capacityBytes = 64 * 1024;
        var store = CreateStore(mapName, mutexName, capacityBytes, TimeSpan.FromMilliseconds(50));

        using var holderMutex = new Mutex(false, mutexName);
        using var mutexAcquired = new ManualResetEventSlim(false);
        using var releaseMutex = new ManualResetEventSlim(false);
        var holderThread = new Thread(() =>
        {
            holderMutex.WaitOne();
            mutexAcquired.Set();
            releaseMutex.Wait();
            holderMutex.ReleaseMutex();
        });

        holderThread.Start();
        Assert.True(mutexAcquired.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = Stopwatch.StartNew();
        store.Upsert(new
        {
            instanceId = $"instance-{suffix}",
            pipeName = "pipe-locked",
            processId = 987,
            solutionPath = "",
            solutionName = "",
        });
        stopwatch.Stop();

        releaseMutex.Set();
        holderThread.Join(TimeSpan.FromSeconds(2));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(400),
            $"Upsert took {stopwatch.ElapsedMilliseconds}ms while lock timeout should be bounded.");
    }

    private static MemoryDiscoveryStore CreateStore(
        string mapName,
        string mutexName,
        int capacityBytes,
        TimeSpan lockTimeout,
        string? mapFilePath = null)
    {
        return new MemoryDiscoveryStore(
            mapName,
            mutexName,
            capacityBytes,
            lockTimeout,
            static name => new Mutex(false, name),
            (name, capacity) =>
            {
                if (string.IsNullOrWhiteSpace(mapFilePath))
                {
                    return MemoryMappedFile.CreateOrOpen(name, capacity, MemoryMappedFileAccess.ReadWrite);
                }

                return MemoryMappedFile.CreateFromFile(
                    mapFilePath,
                    FileMode.OpenOrCreate,
                    name,
                    capacity,
                    MemoryMappedFileAccess.ReadWrite);
            });
    }

    private static JObject? ReadRootFromFile(string path, int capacityBytes)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var lenBuffer = new byte[4];
            var bytesRead = stream.Read(lenBuffer, 0, lenBuffer.Length);
            if (bytesRead < lenBuffer.Length)
            {
                return null;
            }

            var payloadLength = BitConverter.ToInt32(lenBuffer, 0);
            if (payloadLength <= 0 || payloadLength > capacityBytes - 4)
            {
                return null;
            }

            var payload = new byte[payloadLength];
            bytesRead = stream.Read(payload, 0, payload.Length);
            if (bytesRead != payloadLength)
            {
                return null;
            }

            return JObject.Parse(Encoding.UTF8.GetString(payload));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JObject> GetItems(JObject root)
    {
        return (root["items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
    }
}
