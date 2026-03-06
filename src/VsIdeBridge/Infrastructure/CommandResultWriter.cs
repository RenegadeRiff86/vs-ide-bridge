using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VsIdeBridge.Infrastructure;

internal static class CommandResultWriter
{
    public static async Task WriteAsync(string outputPath, CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var normalizedPath = PathNormalization.NormalizeFilePath(outputPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new CommandErrorException("output_write_failed", "Output path is empty.");
        }

        var directory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new CommandErrorException("output_write_failed", $"Could not determine output directory from '{normalizedPath}'.");
        }

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, Path.GetRandomFileName());
        var json = JsonConvert.SerializeObject(envelope, Formatting.Indented);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(normalizedPath))
            {
                File.Delete(normalizedPath);
            }

            File.Move(tempPath, normalizedPath);
        }
        catch (Exception ex)
        {
            throw new CommandErrorException("output_write_failed", $"Failed to write output file '{normalizedPath}'.", new { exception = ex.Message });
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }
}
