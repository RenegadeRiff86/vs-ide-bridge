using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("vs-ide-bridge is Windows-only.");
    return 1;
}

try
{
    return await CliApp.RunAsync(args);
}
catch (CliException ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex}");
    return 1;
}

internal static partial class CliApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var rawVerb = args[0].Trim();
        if (IsHelpToken(rawVerb))
        {
            PrintHelp(args.Length > 1 ? args[1] : null);
            return 0;
        }

        var verb = NormalizeVerb(rawVerb);
        var remainingArgs = args.Skip(1).ToArray();
        if (remainingArgs.Any(IsHelpToken))
        {
            PrintHelp(verb);
            return 0;
        }

        var options = CliOptions.Parse(remainingArgs);
        return verb switch
        {
            "parse" => RunParseAsync(options),
            "prompts" => RunPromptHelp(),
            "ensure" => await RunEnsureAsync(options),
            "current" => await RunCurrentAsync(options),
            "instances" => await RunInstancesAsync(options),
            "find-files" => await RunStructuredCommandAsync("find-files", options, static (builder, cli) =>
            {
                builder.AddRequired("query", cli.GetValue("query"));
                builder.Add("path", cli.GetValue("path"));
                builder.Add("extensions", cli.GetValue("extensions"));
                builder.Add("max-results", cli.GetValue("max-results"));
                builder.Add("include-non-project", cli.GetValue("include-non-project"));
            }),
            "find-text" => await RunStructuredCommandAsync("find-text", options, static (builder, cli) =>
            {
                builder.AddRequired("query", cli.GetValue("query"));
                builder.Add("scope", cli.GetValue("scope"));
                builder.Add("project", cli.GetValue("project"));
                builder.Add("path", cli.GetValue("path"));
                builder.Add("results-window", cli.GetValue("results-window"));
                builder.AddFlag("match-case", cli.GetFlag("match-case"));
                builder.AddFlag("whole-word", cli.GetFlag("whole-word"));
                builder.AddFlag("regex", cli.GetFlag("regex"));
            }),
            "open-document" => await RunStructuredCommandAsync("open-document", options, static (builder, cli) =>
            {
                builder.AddRequired("file", cli.GetValue("file"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
                builder.Add("allow-disk-fallback", cli.GetValue("allow-disk-fallback"));
            }),
            "list-documents" => await RunStructuredCommandAsync("list-documents", options, static (_, _) => { }),
            "list-tabs" => await RunStructuredCommandAsync("list-tabs", options, static (_, _) => { }),
            "activate-document" => await RunStructuredCommandAsync("activate-document", options, static (builder, cli) =>
            {
                builder.AddRequired("query", cli.GetValue("query"));
            }),
            "close-document" => await RunStructuredCommandAsync("close-document", options, static (builder, cli) =>
            {
                builder.Add("query", cli.GetValue("query"));
                builder.AddFlag("all", cli.GetFlag("all"));
                builder.AddFlag("save", cli.GetFlag("save"));
            }),
            "save-document" => await RunStructuredCommandAsync("save-document", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.AddFlag("all", cli.GetFlag("all"));
            }),
            "close-file" => await RunStructuredCommandAsync("close-file", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("query", cli.GetValue("query"));
                builder.AddFlag("save", cli.GetFlag("save"));
            }),
            "close-others" => await RunStructuredCommandAsync("close-others", options, static (builder, cli) =>
            {
                builder.AddFlag("save", cli.GetFlag("save"));
            }),
            "list-windows" => await RunStructuredCommandAsync("list-windows", options, static (builder, cli) =>
            {
                builder.Add("query", cli.GetValue("query"));
            }),
            "activate-window" => await RunStructuredCommandAsync("activate-window", options, static (builder, cli) =>
            {
                builder.AddRequired("window", cli.GetValue("window"));
            }),
            "apply-diff" => await RunStructuredCommandAsync("apply-diff", options, static (builder, cli) =>
            {
                builder.Add("patch-file", cli.GetValue("patch-file"));
                builder.Add("patch-text-base64", cli.GetValue("patch-text-base64"));
                builder.Add("base-directory", cli.GetValue("base-directory"));
                builder.AddFlag("open-changed-files", cli.GetFlag("open-changed-files"));
                builder.AddFlag("save-changed-files", cli.GetFlag("save-changed-files"));
            }),
            "document-slice" => await RunStructuredCommandAsync("document-slice", options, static (builder, cli) =>
            {
                builder.AddRequired("file", cli.GetValue("file"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("context-before", cli.GetValue("context-before"));
                builder.Add("context-after", cli.GetValue("context-after"));
                builder.Add("start-line", cli.GetValue("start-line"));
                builder.Add("end-line", cli.GetValue("end-line"));
            }),
            "search-symbols" => await RunStructuredCommandAsync("search-symbols", options, static (builder, cli) =>
            {
                builder.AddRequired("query", cli.GetValue("query"));
                builder.Add("kind", cli.GetValue("kind"));
                builder.Add("scope", cli.GetValue("scope"));
                builder.Add("path", cli.GetValue("path"));
                builder.Add("max", cli.GetValue("max"));
                builder.Add("project", cli.GetValue("project"));
                builder.AddFlag("match-case", cli.GetFlag("match-case"));
            }),
            "goto-definition" => await RunStructuredCommandAsync("goto-definition", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
            }),
            "peek-definition" => await RunStructuredCommandAsync("peek-definition", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
                builder.Add("context-lines", cli.GetValue("context-lines"));
            }),
            "goto-implementation" => await RunStructuredCommandAsync("goto-implementation", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
            }),
            "find-references" => await RunStructuredCommandAsync("find-references", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.AddFlag("select-word", cli.GetFlag("select-word"));
                builder.AddFlag("activate-window", cli.GetFlag("activate-window"));
            }),
            "count-references" => await RunStructuredCommandAsync("count-references", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.AddFlag("select-word", cli.GetFlag("select-word"));
                builder.AddFlag("activate-window", cli.GetFlag("activate-window"));
            }),
            "call-hierarchy" => await RunStructuredCommandAsync("call-hierarchy", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.AddFlag("select-word", cli.GetFlag("select-word"));
                builder.AddFlag("activate-window", cli.GetFlag("activate-window"));
            }),
            "quick-info" => await RunStructuredCommandAsync("quick-info", options, static (builder, cli) =>
            {
                builder.Add("file", cli.GetValue("file"));
                builder.Add("document", cli.GetValue("document"));
                builder.Add("line", cli.GetValue("line"));
                builder.Add("column", cli.GetValue("column"));
                builder.Add("context-lines", cli.GetValue("context-lines"));
            }),
            "document-slices" => await RunStructuredCommandAsync("document-slices", options, static (builder, cli) =>
            {
                var ranges = cli.GetValue("ranges");
                var rangesFile = cli.GetValue("ranges-file");
                if (string.IsNullOrWhiteSpace(ranges) && string.IsNullOrWhiteSpace(rangesFile))
                {
                    throw new CliException("Specify --ranges-file <file> or --ranges <json>.");
                }

                builder.Add("ranges-file", rangesFile);
                builder.Add("ranges", ranges);
            }),
            "file-symbols" => await RunStructuredCommandAsync("file-symbols", options, static (builder, cli) =>
            {
                builder.AddRequired("file", cli.GetValue("file"));
                builder.Add("kind", cli.GetValue("kind"));
                builder.Add("max-depth", cli.GetValue("max-depth"));
            }),
            "file-outline" => await RunStructuredCommandAsync("file-outline", options, static (builder, cli) =>
            {
                builder.AddRequired("file", cli.GetValue("file"));
                builder.Add("kind", cli.GetValue("kind"));
                builder.Add("max-depth", cli.GetValue("max-depth"));
            }),
            "debug-threads" => await RunStructuredCommandAsync("debug-threads", options, static (_, _) => { }),
            "debug-stack" => await RunStructuredCommandAsync("debug-stack", options, static (builder, cli) =>
            {
                builder.Add("thread-id", cli.GetValue("thread-id"));
                builder.Add("max-frames", cli.GetValue("max-frames"));
            }),
            "debug-locals" => await RunStructuredCommandAsync("debug-locals", options, static (builder, cli) =>
            {
                builder.Add("max", cli.GetValue("max"));
            }),
            "debug-modules" => await RunStructuredCommandAsync("debug-modules", options, static (_, _) => { }),
            "debug-watch" => await RunStructuredCommandAsync("debug-watch", options, static (builder, cli) =>
            {
                builder.AddRequired("expression", cli.GetValue("expression"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
            }),
            "debug-exceptions" => await RunStructuredCommandAsync("debug-exceptions", options, static (_, _) => { }),
            "diagnostics-snapshot" => await RunStructuredCommandAsync("diagnostics-snapshot", options, static (builder, cli) =>
            {
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.Add("max", cli.GetValue("max"));
                builder.AddFlag("wait-for-intellisense", cli.GetFlag("wait-for-intellisense"));
                builder.AddFlag("quick", cli.GetFlag("quick"));
            }),
            "build-configurations" => await RunStructuredCommandAsync("build-configurations", options, static (_, _) => { }),
            "set-build-configuration" => await RunStructuredCommandAsync("set-build-configuration", options, static (builder, cli) =>
            {
                builder.AddRequired("configuration", cli.GetValue("configuration"));
                builder.Add("platform", cli.GetValue("platform"));
            }),
            "build" => await RunStructuredCommandAsync("build", options, static (builder, cli) =>
            {
                builder.Add("configuration", cli.GetValue("configuration"));
                builder.Add("platform", cli.GetValue("platform"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
            }),
            "catalog" => await RunSimpleCommandAsync("Tools.IdeHelp", options, "summary"),
            "ready" => await RunSimpleCommandAsync("Tools.IdeWaitForReady", options),
            "state" => await RunSimpleCommandAsync("Tools.IdeGetState", options),
            "errors" => await RunStructuredCommandAsync("errors", options, static (builder, cli) =>
            {
                builder.Add("severity", cli.GetValue("severity"));
                builder.Add("code", cli.GetValue("code"));
                builder.Add("project", cli.GetValue("project"));
                builder.Add("path", cli.GetValue("path"));
                builder.Add("text", cli.GetValue("text"));
                builder.Add("group-by", cli.GetValue("group-by"));
                builder.Add("max", cli.GetValue("max"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.AddFlag("wait-for-intellisense", cli.GetFlag("wait-for-intellisense"));
                builder.AddFlag("quick", cli.GetFlag("quick"));
            }),
            "warnings" => await RunStructuredCommandAsync("warnings", options, static (builder, cli) =>
            {
                builder.Add("severity", cli.GetValue("severity"));
                builder.Add("code", cli.GetValue("code"));
                builder.Add("project", cli.GetValue("project"));
                builder.Add("path", cli.GetValue("path"));
                builder.Add("text", cli.GetValue("text"));
                builder.Add("group-by", cli.GetValue("group-by"));
                builder.Add("max", cli.GetValue("max"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.AddFlag("wait-for-intellisense", cli.GetFlag("wait-for-intellisense"));
                builder.AddFlag("quick", cli.GetFlag("quick"));
            }),
            "build-errors" => await RunStructuredCommandAsync("build-errors", options, static (builder, cli) =>
            {
                builder.Add("severity", cli.GetValue("severity"));
                builder.Add("code", cli.GetValue("code"));
                builder.Add("project", cli.GetValue("project"));
                builder.Add("path", cli.GetValue("path"));
                builder.Add("text", cli.GetValue("text"));
                builder.Add("group-by", cli.GetValue("group-by"));
                builder.Add("max", cli.GetValue("max"));
                builder.Add("timeout-ms", cli.GetValue("timeout-ms"));
                builder.AddFlag("wait-for-intellisense", cli.GetFlag("wait-for-intellisense"));
            }),
            "send" or "call" => await RunSendAsync(options),
            "close" => await RunSimpleCommandAsync("close-ide", options, "summary"),
            "batch" => await RunBatchAsync(options),
            "request" => await RunRawRequestAsync(options),
            "mcp-server" => await RunMcpServerAsync(options),
            _ => throw new CliException($"Unknown command '{verb}'. Run 'vs-ide-bridge help'."),
        };
    }

    private static bool IsHelpToken(string? token)
    {
        return token is "help" or "--help" or "-h" or "/?";
    }

    private static string NormalizeVerb(string verb)
    {
        return verb.Trim().ToLowerInvariant() switch
        {
            "call" => "send",
            "commands" => "catalog",
            "patch" => "apply-diff",
            "peek" => "peek-definition",
            "recipes" => "prompts",
            "slice" => "document-slice",
            "slices" => "document-slices",
            "symbols" => "search-symbols",
            "warning" => "warnings",
            _ => verb.Trim().ToLowerInvariant(),
        };
    }

    private static int RunPromptHelp()
    {
        PrintHelp("prompts");
        return 0;
    }

    private static int RunParseAsync(CliOptions options)
    {
        var jsonText = options.GetValue("json");
        var jsonFile = options.GetValue("json-file");
        if (string.IsNullOrWhiteSpace(jsonText) == string.IsNullOrWhiteSpace(jsonFile))
        {
            throw new CliException("Specify exactly one of --json or --json-file.");
        }

        if (!string.IsNullOrWhiteSpace(jsonFile))
        {
            jsonText = File.ReadAllText(jsonFile!);
        }

        var node = ParseJson(jsonText!);
        var select = options.GetValue("select") ?? options.GetValue("path");
        if (select is not null && select.Length >= 3 && char.IsLetter(select[0]) && select[1] == ':' && (select[2] == '/' || select[2] == '\\'))
        {
            throw new CliException(
                $"--select value looks like a Windows path ('{select}'). " +
                "Git Bash converts leading '/' in arguments to a Windows path. " +
                "Either omit the leading slash (e.g. Data/foo instead of /Data/foo) " +
                "or prefix the command with MSYS_NO_PATHCONV=1.");
        }

        var selected = SelectJsonNode(node, select);
        var format = options.GetValue("format") ?? ParseFormatter.GetDefaultFormat(selected, select);
        var text = ParseFormatter.Format(selected, format, select);
        var outputPath = options.GetValue("out");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(outputPath!, text, new UTF8Encoding(false));
        }

        Console.WriteLine(text);
        return 0;
    }

    private static async Task<int> RunCurrentAsync(CliOptions options)
    {
        var discoveryMode = ResolveDiscoveryMode(options);
        var instance = await PipeDiscovery.SelectAsync(
            BridgeInstanceSelector.FromOptions(options),
            options.GetFlag("verbose"),
            discoveryMode);
        Console.WriteLine(InstanceFormatter.FormatSingle(instance, options.GetValue("format") ?? "summary"));
        return 0;
    }

    private static async Task<int> RunEnsureAsync(CliOptions options)
    {
        var solution = options.GetValue("solution") ?? options.GetValue("sln");
        if (string.IsNullOrWhiteSpace(solution))
        {
            throw new CliException("Missing required option --solution.");
        }

        var solutionPath = NormalizeExistingSolutionPath(solution);
        var verbose = options.GetFlag("verbose");
        var timeoutMs = Math.Max(1_000, options.GetInt32("timeout-ms", 180_000));
        var pollMs = Math.Max(100, options.GetInt32("poll-ms", 1_000));
        var waitForReady = !options.GetFlag("skip-ready");
        var discoveryMode = ResolveDiscoveryMode(options);
        var emitDiscoveryJson = options.GetBoolean("emit-discovery-json", true);

        var discovery = await TryFindSolutionInstanceAsync(solutionPath, discoveryMode).ConfigureAwait(false);
        if (discovery is null)
        {
            StartVisualStudio(solutionPath, verbose, discoveryMode, emitDiscoveryJson);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(pollMs).ConfigureAwait(false);
                discovery = await TryFindSolutionInstanceAsync(solutionPath, discoveryMode).ConfigureAwait(false);
                if (discovery is not null)
                {
                    break;
                }
            }
        }

        if (discovery is null)
        {
            throw new CliException(
                $"Timed out waiting for a bridge instance for '{solutionPath}'. " +
                "Open Visual Studio with the VS IDE Bridge extension installed and try again.");
        }

        if (waitForReady)
        {
            var readyPayload = CreateCommandRequest("Tools.IdeWaitForReady", string.Empty, "ready");
            await ExecuteAsync(readyPayload, discovery, options, "summary", writeOutput: false).ConfigureAwait(false);
        }

        var statePayload = CreateCommandRequest("Tools.IdeGetState", string.Empty, options.GetValue("request-id"));
        return await ExecuteAsync(statePayload, discovery, options, "summary").ConfigureAwait(false);
    }

    private static async Task<int> RunInstancesAsync(CliOptions options)
    {
        var selector = BridgeInstanceSelector.FromOptions(options);
        var instances = await PipeDiscovery.ListAsync(options.GetFlag("verbose"), ResolveDiscoveryMode(options));
        var filtered = PipeDiscovery.Filter(instances, selector).ToArray();

        Console.WriteLine(InstanceFormatter.Format(filtered, options.GetValue("format") ?? "summary"));
        return 0;
    }

    private static async Task<int> RunSendAsync(CliOptions options)
    {
        var command = options.GetRequiredValue("command");
        return await RunSimpleCommandAsync(command, options);
    }

    private static async Task<int> RunStructuredCommandAsync(
        string command,
        CliOptions options,
        Action<PipeArgsBuilder, CliOptions> configure,
        string defaultFormat = "json")
    {
        var builder = new PipeArgsBuilder();
        configure(builder, options);
        return await RunSimpleCommandAsync(command, builder.Build(), options, defaultFormat);
    }

    private static async Task<int> RunSimpleCommandAsync(string command, CliOptions options, string defaultFormat = "json")
    {
        var args = options.GetValue("args") ?? string.Empty;
        return await RunSimpleCommandAsync(command, args, options, defaultFormat);
    }

    private static async Task<int> RunSimpleCommandAsync(
        string command,
        string args,
        CliOptions options,
        string defaultFormat = "json")
    {
        var requestId = options.GetValue("request-id");
        JsonObject payload;

        if (options.GetFlag("wait-for-ready"))
        {
            var steps = new JsonArray
            {
                CreateCommandRequest("Tools.IdeWaitForReady", string.Empty, "ready"),
                CreateCommandRequest(command, args, "command"),
            };
            payload = CreateBatchRequest(steps, options.GetFlag("stop-on-error"), requestId);
        }
        else
        {
            payload = CreateCommandRequest(command, args, requestId);
        }

        return await ExecuteAsync(payload, options, defaultFormat);
    }

    private static async Task<int> RunBatchAsync(CliOptions options)
    {
        var filePath = options.GetRequiredValue("file");
        var node = ParseJson(File.ReadAllText(filePath));
        JsonObject payload = node switch
        {
            JsonArray array => CreateBatchRequest(NormalizeBatchArray(array), options.GetFlag("stop-on-error"), options.GetValue("request-id")),
            JsonObject obj => NormalizeRawRequest(obj, options),
            _ => throw new CliException("Batch input must be a JSON array or request object."),
        };

        return await ExecuteAsync(payload, options);
    }

    private static async Task<int> RunRawRequestAsync(CliOptions options)
    {
        var jsonText = options.GetValue("json");
        var jsonFile = options.GetValue("json-file");
        if (string.IsNullOrWhiteSpace(jsonText) == string.IsNullOrWhiteSpace(jsonFile))
        {
            throw new CliException("Specify exactly one of --json or --json-file.");
        }

        if (!string.IsNullOrWhiteSpace(jsonFile))
        {
            jsonText = File.ReadAllText(jsonFile!);
        }

        var node = ParseJson(jsonText!);
        JsonObject payload = node switch
        {
            JsonObject obj => NormalizeRawRequest(obj, options),
            JsonArray array => CreateBatchRequest(NormalizeBatchArray(array), options.GetFlag("stop-on-error"), options.GetValue("request-id")),
            _ => throw new CliException("Raw request JSON must be an object or array."),
        };

        return await ExecuteAsync(payload, options);
    }

    private static async Task<int> ExecuteAsync(JsonObject payload, CliOptions options, string defaultFormat = "json")
    {
        var discovery = await PipeDiscovery.SelectAsync(
            BridgeInstanceSelector.FromOptions(options),
            options.GetFlag("verbose"),
            ResolveDiscoveryMode(options));
        return await ExecuteAsync(payload, discovery, options, defaultFormat).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(
        JsonObject payload,
        PipeDiscovery discovery,
        CliOptions options,
        string defaultFormat = "json",
        bool writeOutput = true)
    {
        await using var client = new PipeClient(discovery.PipeName, options.GetInt32("timeout-ms", 10_000));

        JsonObject response;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            response = await client.SendAsync(payload).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new CliException(ex.Message);
        }
        stopwatch.Stop();
        response["_elapsedMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);

        var outputPath = options.GetValue("out");
        if (writeOutput && !string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(outputPath!, response.ToJsonString(JsonOptions), Encoding.UTF8);
        }

        if (writeOutput)
        {
            Console.WriteLine(ResponseFormatter.Format(response, options.GetValue("format") ?? defaultFormat));
        }

        return ResponseFormatter.IsSuccess(response) ? 0 : 1;
    }

    private static JsonObject CreateCommandRequest(string command, string args, string? id)
    {
        return new JsonObject
        {
            ["id"] = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N")[..8] : id,
            ["command"] = command,
            ["args"] = args,
        };
    }

    private static JsonObject CreateBatchRequest(JsonArray batch, bool stopOnError, string? requestId)
    {
        return new JsonObject
        {
            ["id"] = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N")[..8] : requestId,
            ["command"] = "Tools.IdeBatchCommands",
            ["batch"] = batch,
            ["stopOnError"] = stopOnError,
        };
    }

    private static JsonArray NormalizeBatchArray(JsonArray array)
    {
        var batch = new JsonArray();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject step)
            {
                throw new CliException($"Batch entry {i} must be an object.");
            }

            var command = step["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new CliException($"Batch entry {i} is missing 'command'.");
            }

            var id = step["id"]?.GetValue<string>();
            var args = step["args"]?.GetValue<string>() ?? string.Empty;
            batch.Add(CreateCommandRequest(command!, args, id));
        }

        return batch;
    }

    private static JsonObject NormalizeRawRequest(JsonObject request, CliOptions options)
    {
        if (request["batch"] is JsonArray batch)
        {
            request["batch"] = NormalizeBatchArray(batch);
            if (request["stopOnError"] is null && options.GetFlag("stop-on-error"))
            {
                request["stopOnError"] = true;
            }
        }

        if (request["id"] is null)
        {
            request["id"] = options.GetValue("request-id") ?? Guid.NewGuid().ToString("N")[..8];
        }

        return request;
    }

    private static JsonNode ParseJson(string json)
    {
        return JsonNode.Parse(json) ?? throw new CliException("JSON payload was empty.");
    }

    private static JsonNode? SelectJsonNode(JsonNode root, string? select)
    {
        var segments = ParseSelectSegments(select);
        return SelectJsonNode(root, segments, 0, "/");
    }

    private static string[] ParseSelectSegments(string? select)
    {
        if (string.IsNullOrWhiteSpace(select) || string.Equals(select, "/", StringComparison.Ordinal))
        {
            return [];
        }

        return select
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonNode? SelectJsonNode(JsonNode? node, string[] segments, int index, string currentPath)
    {
        if (index >= segments.Length)
        {
            return node?.DeepClone();
        }

        var segment = segments[index];
        if (segment == "*")
        {
            return SelectWildcard(node, segments, index + 1, currentPath);
        }

        return node switch
        {
            JsonObject obj => SelectObjectProperty(obj, segment, segments, index + 1, currentPath),
            JsonArray array => SelectArrayIndex(array, segment, segments, index + 1, currentPath),
            null => throw new CliException($"Path '{currentPath}' is null and has no child '{segment}'."),
            _ => throw new CliException($"Path '{currentPath}' is a value and has no child '{segment}'."),
        };
    }

    private static JsonArray SelectWildcard(JsonNode? node, string[] segments, int nextIndex, string currentPath)
    {
        var results = new JsonArray();
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var childPath = $"{currentPath.TrimEnd('/')}/{i}";
                    results.Add(SelectJsonNode(array[i], segments, nextIndex, childPath));
                }

                return results;
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    var childPath = $"{currentPath.TrimEnd('/')}/{pair.Key}";
                    results.Add(SelectJsonNode(pair.Value, segments, nextIndex, childPath));
                }

                return results;
            case null:
                throw new CliException($"Path '{currentPath}' is null and cannot use '*'.");
            default:
                throw new CliException($"Path '{currentPath}' is a value and cannot use '*'.");
        }
    }

    private static JsonNode? SelectObjectProperty(
        JsonObject obj,
        string segment,
        string[] segments,
        int nextIndex,
        string currentPath)
    {
        if (!obj.TryGetPropertyValue(segment, out var child))
        {
            throw new CliException($"Property '{segment}' was not found at '{currentPath}'.");
        }

        var childPath = $"{currentPath.TrimEnd('/')}/{segment}";
        return SelectJsonNode(child, segments, nextIndex, childPath);
    }

    private static JsonNode? SelectArrayIndex(
        JsonArray array,
        string segment,
        string[] segments,
        int nextIndex,
        string currentPath)
    {
        if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw new CliException($"Expected an array index at '{currentPath}', got '{segment}'.");
        }

        if (index < 0 || index >= array.Count)
        {
            throw new CliException($"Array index '{index}' is out of range at '{currentPath}'.");
        }

        var childPath = $"{currentPath.TrimEnd('/')}/{index}";
        return SelectJsonNode(array[index], segments, nextIndex, childPath);
    }

    private static async Task<PipeDiscovery?> TryFindSolutionInstanceAsync(string solutionPath, DiscoveryMode discoveryMode)
    {
        var solutionName = Path.GetFileName(solutionPath);
        var instances = await PipeDiscovery.ListAsync(verbose: false, discoveryMode).ConfigureAwait(false);
        return instances
            .Where(instance =>
                string.Equals(NormalizeExistingPathOrEmpty(instance.SolutionPath), solutionPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(instance.SolutionName, solutionName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(instance => instance.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string NormalizeExistingSolutionPath(string path)
    {
        var fullPath = NormalizeExistingPathOrEmpty(path);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            throw new CliException($"Solution not found: {path}");
        }

        return fullPath;
    }

    private static string NormalizeExistingPathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private static void StartVisualStudio(string solutionPath, bool verbose, DiscoveryMode discoveryMode, bool emitDiscoveryJson)
    {
        var devenvPath = ResolveDevenvPath();
        if (verbose)
        {
            Console.Error.WriteLine($"Starting Visual Studio: {solutionPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = devenvPath,
            Arguments = QuoteArgument(solutionPath),
            WorkingDirectory = Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
        };
        startInfo.EnvironmentVariables["VS_IDE_BRIDGE_DISCOVERY_MODE"] = DiscoveryModeToOption(discoveryMode);
        startInfo.EnvironmentVariables["VS_IDE_BRIDGE_EMIT_DISCOVERY_JSON"] = emitDiscoveryJson ? "true" : "false";

        _ = Process.Start(startInfo)
            ?? throw new CliException($"Failed to start Visual Studio for '{solutionPath}'.");
    }

    private static DiscoveryMode ResolveDiscoveryMode(CliOptions options)
    {
        var raw = options.GetValue("discovery-mode")
            ?? Environment.GetEnvironmentVariable("VS_IDE_BRIDGE_DISCOVERY_MODE")
            ?? "memory-first";

        return raw.Trim().ToLowerInvariant() switch
        {
            "json-only" => DiscoveryMode.JsonOnly,
            "hybrid" => DiscoveryMode.Hybrid,
            "memory-first" => DiscoveryMode.MemoryFirst,
            _ => throw new CliException("Invalid --discovery-mode. Use json-only, hybrid, or memory-first."),
        };
    }

    private static string DiscoveryModeToOption(DiscoveryMode mode)
    {
        return mode switch
        {
            DiscoveryMode.JsonOnly => "json-only",
            DiscoveryMode.Hybrid => "hybrid",
            _ => "memory-first",
        };
    }

    private static string ResolveDevenvPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Enterprise", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe"),
            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Enterprise", "Common7", "IDE", "devenv.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new CliException("devenv.exe not found.");
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private static void PrintHelp(string? subject = null)
    {
        var normalizedSubject = string.IsNullOrWhiteSpace(subject) ? string.Empty : NormalizeVerb(subject);
        var text = normalizedSubject switch
        {
            "" => HelpText.General,
            "workflow" => HelpText.Workflow,
            "parse" => HelpText.Parse,
            "prompts" => HelpText.Prompts,
            "ensure" => HelpText.Ensure,
            "current" => HelpText.Current,
            "instances" => HelpText.Instances,
            "catalog" => HelpText.Catalog,
            "ready" => HelpText.Ready,
            "state" => HelpText.State,
            "build" => HelpText.Build,
            "errors" => HelpText.Errors,
            "warnings" => HelpText.Warnings,
            "build-errors" => HelpText.BuildErrors,
            "find-files" => HelpText.FindFiles,
            "find-text" => HelpText.FindText,
            "open-document" => HelpText.OpenDocument,
            "list-documents" => HelpText.ListDocuments,
            "list-tabs" => HelpText.ListTabs,
            "activate-document" => HelpText.ActivateDocument,
            "close-document" => HelpText.CloseDocument,
            "save-document" => HelpText.SaveDocument,
            "close-file" => HelpText.CloseFile,
            "close-others" => HelpText.CloseOthers,
            "list-windows" => HelpText.ListWindows,
            "activate-window" => HelpText.ActivateWindow,
            "apply-diff" => HelpText.ApplyDiff,
            "document-slice" => HelpText.DocumentSlice,
            "document-slices" => HelpText.DocumentSlices,
            "search-symbols" => HelpText.SearchSymbols,
            "goto-definition" => HelpText.GoToDefinition,
            "peek-definition" => HelpText.PeekDefinition,
            "goto-implementation" => HelpText.GoToImplementation,
            "find-references" => HelpText.FindReferences,
            "count-references" => HelpText.CountReferences,
            "call-hierarchy" => HelpText.CallHierarchy,
            "quick-info" => HelpText.QuickInfo,
            "file-symbols" => HelpText.FileSymbols,
            "file-outline" => HelpText.FileSymbols,
            "debug-threads" => HelpText.DebugThreads,
            "debug-stack" => HelpText.DebugStack,
            "debug-locals" => HelpText.DebugLocals,
            "debug-modules" => HelpText.DebugModules,
            "debug-watch" => HelpText.DebugWatch,
            "debug-exceptions" => HelpText.DebugExceptions,
            "diagnostics-snapshot" => HelpText.DiagnosticsSnapshot,
            "build-configurations" => HelpText.BuildConfigurations,
            "set-build-configuration" => HelpText.SetBuildConfiguration,
            "close" => HelpText.Close,
            "send" => HelpText.Send,
            "batch" => HelpText.Batch,
            "request" => HelpText.Request,
            "mcp-server" => HelpText.McpServer,
            _ => $"ERROR: Unknown help topic '{subject}'.{Environment.NewLine}{Environment.NewLine}{HelpText.General}",
        };

        Console.WriteLine(text);
    }
}

internal static class HelpText
{
    public const string General =
        """
        vs-ide-bridge <command> [options]

        LLM refresher
          1. vs-ide-bridge help
          2. vs-ide-bridge ensure --solution C:\repo\Your.sln
          3. copy instanceId
          4. vs-ide-bridge catalog --instance <instanceId>
          5. vs-ide-bridge find-files --instance <instanceId> --query foo.cpp

        Main commands
          ensure          Reuse or start Visual Studio for one solution
          current         Resolve one live bridge instance
          instances       List all live bridge instances
          prompts         Print common LLM command recipes
          parse           Parse JSON with slash-path selection
          catalog         List bridge commands from the live IDE
          find-files      Find files by name
          find-text       Find text with optional subtree filtering
          goto-definition Jump to one definition
          peek-definition Get definition context without leaving the source location
          goto-implementation Jump to one implementation
          find-references Run Find All References
          count-references Count references (exact-or-explicit)
          call-hierarchy  Open Call Hierarchy
          list-documents  List open documents from the IDE document table
          list-tabs       List open editor tabs
          activate-document Activate one open document by path or name
          close-document  Close one matching document, or all open documents
          save-document   Save one document or all open documents
          close-file      Close one open file tab
          close-others    Close all tabs except the active one
          list-windows    List open tool/document windows
          activate-window Activate one window by caption fragment
          search-symbols  Search symbols semantically by name
          quick-info      Resolve symbol info at a location
          debug-threads   Capture debugger thread snapshot
          debug-stack     Capture debugger stack frames
          debug-locals    Capture local variables in break mode
          debug-modules   Capture debugger modules snapshot
          debug-watch     Evaluate one debugger watch expression
          debug-exceptions Capture debugger exception settings snapshot
          diagnostics-snapshot Capture IDE/debug/build/error snapshot
          build-configurations List available build configs/platforms
          set-build-configuration Activate one build config/platform
          file-symbols    List symbols from one file
          file-outline    List symbols from one file (alias for file-symbols)
          apply-diff      Apply a unified diff visibly in Visual Studio
          document-slice  Fetch one code slice
          document-slices Fetch multiple code slices
          open-document   Open a file at a location
          ready           Wait for the IDE to be ready
          state           Capture IDE state
          build           Build the current solution
          errors          Capture the Error List
          warnings        Capture warning rows only
          build-errors    Build and capture errors
          close           Close Visual Studio via the DTE API
          send            Send one bridge command
          call            Alias for send
          batch           Send a batch request
          request         Send raw JSON
          mcp-server      Run MCP stdio server facade over the bridge

        Common selectors
          --instance <id> exact instance id
          --pid <pid>     Visual Studio process id
          --pipe <name>   pipe name
          --sln <hint>    solution path or name substring

        Common flags
          --format json|summary|keyvalue
          --timeout-ms <ms>
          --out <file>
          --discovery-mode json-only|hybrid|memory-first
          --emit-discovery-json true|false
          --verbose

        Help topics
          vs-ide-bridge help workflow
          vs-ide-bridge help prompts
          vs-ide-bridge help ensure
          vs-ide-bridge help current
          vs-ide-bridge help parse
          vs-ide-bridge help catalog
          vs-ide-bridge help search-symbols
          vs-ide-bridge help peek-definition
          vs-ide-bridge help goto-implementation
          vs-ide-bridge help count-references
          vs-ide-bridge help warnings
          vs-ide-bridge help quick-info
          vs-ide-bridge help debug-watch
          vs-ide-bridge help diagnostics-snapshot
          vs-ide-bridge help build-configurations
          vs-ide-bridge help set-build-configuration
          vs-ide-bridge help list-documents
          vs-ide-bridge help list-tabs
          vs-ide-bridge help activate-document
          vs-ide-bridge help close-document
          vs-ide-bridge help save-document
          vs-ide-bridge help list-windows
          vs-ide-bridge help activate-window
          vs-ide-bridge help close-file
          vs-ide-bridge help apply-diff
          vs-ide-bridge help send
          vs-ide-bridge help batch
        """;

    public const string Workflow =
        """
        Recommended workflow

          vs-ide-bridge help
          vs-ide-bridge prompts
          vs-ide-bridge ensure --solution C:\repo\Your.sln
          vs-ide-bridge current
          vs-ide-bridge catalog --instance <instanceId>
          vs-ide-bridge find-files --instance <instanceId> --query foo.cpp

        Rules
          Use help when you need a refresher.
          Use ensure when you know the solution path.
          Use current when Visual Studio is already running and you just need the live instance id.
          Reuse the same --instance for the rest of the task.
          If current says there are multiple instances, run instances and choose one.
          Prefer the first-class verbs over send when they exist.
          Do not write wrapper scripts unless you are intentionally automating repeated calls.
        """;

    public const string Prompts =
        """
        prompts

        Purpose
          Print task-oriented recipes an LLM can copy with minimal editing.

        Recipes
          Find a symbol definition
            vs-ide-bridge search-symbols --instance <instanceId> --query propose_export_file_name_and_path --kind function

          Peek the definition at a location without leaving the current file
            vs-ide-bridge peek-definition --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13

          Inspect a symbol at a location
            vs-ide-bridge quick-info --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13

          Fetch multiple slices in one call
            vs-ide-bridge document-slices --instance <instanceId> --ranges-file output\ranges.json

          Scope a text search to a subtree
            vs-ide-bridge find-text --instance <instanceId> --query OnInit --path src\libslic3r

          List only function symbols in one file
            vs-ide-bridge file-symbols --instance <instanceId> --file C:\repo\src\foo.cpp --kind function

          Group warnings by code
            vs-ide-bridge warnings --instance <instanceId> --group-by code

          Extract one field from saved bridge output
            vs-ide-bridge parse --json-file output\errors.json --select /Data/errors/rows/0/message
        """;

    public const string Parse =
        """
        parse

        Purpose
          Parse JSON locally so callers do not need ad hoc scripts to inspect bridge output.

        Required
          exactly one of:
            --json <text>
            --json-file <file>

        Optional
          --select <slash-path>
          --format json|value|lines|keyvalue|summary
          --out <file>

        Path rules
          Use slash paths like /Data/errors/rows/0/message
          Use * to map over arrays or objects
          Examples:
            /Data/errors/rows/*/file
            /Data/openTabs/items/0/name

        Examples
          vs-ide-bridge parse --json-file output\errors.json --select /Data/errors/rows/0/message
          vs-ide-bridge parse --json-file output\errors.json --select /Data/errors/rows/*/file --format lines
          vs-ide-bridge parse --json "{\"a\":{\"b\":[1,2]}}" --select /a/b/1
        """;

    public const string Current =
        """
        current

        Purpose
          Resolve one live Visual Studio bridge instance.

        Behavior
          If exactly one instance matches, returns it.
          If multiple instances are live, fails and tells you to run instances.

        Examples
          vs-ide-bridge current
          vs-ide-bridge current --sln VsIdeBridge.sln
          vs-ide-bridge current --format keyvalue
        """;

    public const string Ensure =
        """
        ensure

        Purpose
          Reuse a matching live bridge instance or start Visual Studio for a solution and wait for the bridge.

        Required
          --solution <path-to-sln>

        Optional
          --timeout-ms <ms>
          --poll-ms <ms>
          --skip-ready
          --discovery-mode json-only|hybrid|memory-first
          --emit-discovery-json true|false

        Result
          Prints the state response for the matched instance.
          Exits 0 on success and 1 on failure.

        Examples
          vs-ide-bridge ensure --solution C:\repo\VsIdeBridge.sln
          vs-ide-bridge ensure --solution C:\repo\VsIdeBridge.sln --timeout-ms 180000 --format keyvalue
        """;

    public const string Instances =
        """
        instances

        Purpose
          List all live Visual Studio bridge instances.

        Use when
          current reports multiple live instances.
          You need to pick a specific Visual Studio window.

        Examples
          vs-ide-bridge instances
          vs-ide-bridge instances --format json
          vs-ide-bridge instances --sln Superslicer
        """;

    public const string Catalog =
        """
        catalog

        Purpose
          Ask the live IDE for its registered command catalog.

        Notes
          This wraps the bridge help command so callers do not need to remember the internal name.
          Use --format json to get simple pipe names, legacy names, examples, and recipes.

        Examples
          vs-ide-bridge catalog --instance <instanceId>
          vs-ide-bridge commands --instance <instanceId>
          vs-ide-bridge catalog --instance <instanceId> --format json
        """;

    public const string Ready =
        """
        ready

        Purpose
          Wait for the IDE to be ready for semantic commands.

        Examples
          vs-ide-bridge ready --instance <instanceId>
          vs-ide-bridge ready --instance <instanceId> --timeout-ms 120000
        """;

    public const string State =
        """
        state

        Purpose
          Capture IDE state including solution, active document, caret, open files, and bridge identity.

        Examples
          vs-ide-bridge state --instance <instanceId> --format summary
          vs-ide-bridge state --sln VsIdeBridge.sln --out C:\temp\state.json
        """;

    public const string Build =
        """
        build

        Purpose
          Build the current solution.

        Optional
          --configuration <name>
          --platform <name>
          --timeout-ms <ms>

        Examples
          vs-ide-bridge build --instance <instanceId>
          vs-ide-bridge build --instance <instanceId> --configuration Debug --platform x64
        """;

    public const string Errors =
        """
        errors

        Purpose
          Capture the Visual Studio Error List with optional filtering.

        Optional
          --severity error|warning|message|all
          --code <prefix>
          --project <text>
          --path <text>
          --text <text>
          --group-by code|file|project|tool
          --max <n>
          --quick

        Examples
          vs-ide-bridge errors --instance <instanceId> --format summary
          vs-ide-bridge errors --instance <instanceId> --out C:\temp\errors.json
        """;

    public const string Warnings =
        """
        warnings

        Purpose
          Capture warning rows only, with optional filtering and grouping.

        Optional
          --code <prefix>
          --project <text>
          --path <text>
          --text <text>
          --group-by code|file|project|tool
          --max <n>
          --quick

        Examples
          vs-ide-bridge warnings --instance <instanceId>
          vs-ide-bridge warnings --instance <instanceId> --group-by code
          vs-ide-bridge warnings --instance <instanceId> --code VSTHRD --path src\VsIdeBridge
        """;

    public const string BuildErrors =
        """
        build-errors

        Purpose
          Build the current solution and then capture the Error List.

        Optional
          --severity error|warning|message|all
          --code <prefix>
          --project <text>
          --path <text>
          --text <text>
          --group-by code|file|project|tool
          --max <n>

        Examples
          vs-ide-bridge build-errors --instance <instanceId>
          vs-ide-bridge build-errors --instance <instanceId> --timeout-ms 600000
        """;

    public const string FindFiles =
        """
        find-files

        Purpose
          Search solution explorer files by name or path fragment.

        Required
          --query <text>

        Optional
          --path <text>
          --extensions <csv-or-semicolon-list>
          --max-results <n>
          --include-non-project true|false

        Output
          Returns ranked matches with:
            path, name, project, score, source

        Examples
          vs-ide-bridge find-files --instance <instanceId> --query PipeServerService.cs
          vs-ide-bridge find-files --instance <instanceId> --query CMakeLists.txt --include-non-project true
          vs-ide-bridge find-files --sln VsIdeBridge.sln --query Program.cs
        """;

    public const string FindText =
        """
        find-text

        Purpose
          Search text across the solution, with optional subtree filtering.

        Required
          --query <text>

        Optional
          --path <subdir>
          --scope solution|project|document|open
          --project <name>
          --match-case
          --whole-word
          --regex

        Examples
          vs-ide-bridge find-text --instance <instanceId> --query OnInit
          vs-ide-bridge find-text --instance <instanceId> --query OnInit --path src\libslic3r
        """;

    public const string OpenDocument =
        """
        open-document

        Purpose
          Open a file and optionally move the caret to a line and column.

        Required
          --file <path-or-solution-item>

        Notes
          Accepts an absolute path, solution-relative path, or a solution item name.
          If multiple solution items match, the command returns document_ambiguous.
          Use find-files first when the query is broad.
          --allow-disk-fallback true enables solution-root disk fallback (default true).

        Examples
          vs-ide-bridge open-document --instance <instanceId> --file C:\repo\src\foo.cpp
          vs-ide-bridge open-document --instance <instanceId> --file src\CMakeLists.txt
          vs-ide-bridge open-document --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 1
        """;

    public const string ListDocuments =
        """
        list-documents

        Purpose
          List open documents in Visual Studio.

        Examples
          vs-ide-bridge list-documents --instance <instanceId>
        """;

    public const string ListTabs =
        """
        list-tabs

        Purpose
          List open editor tabs and identify the active one.

        Examples
          vs-ide-bridge list-tabs --instance <instanceId>
        """;

    public const string ActivateDocument =
        """
        activate-document

        Purpose
          Activate an open tab by query.

        Required
          --query <name-or-path-fragment>

        Examples
          vs-ide-bridge activate-document --instance <instanceId> --query Program.cs
        """;

    public const string CloseDocument =
        """
        close-document

        Purpose
          Close one or more open tabs by query.

        Optional
          --query <name-or-path-fragment>
          --all
          --save

        Examples
          vs-ide-bridge close-document --instance <instanceId> --query Program.cs
          vs-ide-bridge close-document --instance <instanceId> --query .json --all
        """;

    public const string SaveDocument =
        """
        save-document

        Purpose
          Save one document by file path or save all open documents.

        Optional
          --file <path>
          --all

        Notes
          If --file is omitted, saves the active document.
          --all saves all open documents and ignores --file.

        Examples
          vs-ide-bridge save-document --instance <instanceId>
          vs-ide-bridge save-document --instance <instanceId> --file C:\repo\src\foo.cpp
          vs-ide-bridge save-document --instance <instanceId> --all
        """;

    public const string CloseFile =
        """
        close-file

        Purpose
          Close one open file by exact path or query.

        Optional
          --file <path>
          --query <name-or-path-fragment>
          --save

        Examples
          vs-ide-bridge close-file --instance <instanceId> --file C:\repo\src\foo.cpp
        """;

    public const string CloseOthers =
        """
        close-others

        Purpose
          Close all open tabs except the active one.

        Optional
          --save

        Examples
          vs-ide-bridge close-others --instance <instanceId>
        """;

    public const string ListWindows =
        """
        list-windows

        Purpose
          List Visual Studio tool windows with optional filtering.

        Optional
          --query <text>

        Examples
          vs-ide-bridge list-windows --instance <instanceId> --query Error List
        """;

    public const string ActivateWindow =
        """
        activate-window

        Purpose
          Activate a tool window by caption or kind.

        Required
          --window <caption-or-kind>

        Examples
          vs-ide-bridge activate-window --instance <instanceId> --window Error List
        """;

    public const string ApplyDiff =
        """
        apply-diff

        Purpose
          Apply a unified diff through the live Visual Studio editor so edits are visible immediately.

        Required
          --patch-file <path>
          or
          --patch-text-base64 <text>

        Optional
          --base-directory <path>
          --open-changed-files
          --save-changed-files

        Notes
          Existing file edits are applied through the editor buffer first.
          This leaves normal edits visible in Visual Studio and lets the IDE re-evaluate syntax.

        Examples
          vs-ide-bridge apply-diff --instance <instanceId> --patch-file C:\temp\change.diff
          vs-ide-bridge apply-diff --instance <instanceId> --patch-file C:\temp\change.diff --save-changed-files
        """;

    public const string DocumentSlice =
        """
        document-slice

        Purpose
          Fetch one code slice from a file.

        Required
          --file <path>

        Optional
          --line <n>
          --context-before <n>
          --context-after <n>
          --start-line <n>
          --end-line <n>

        Examples
          vs-ide-bridge document-slice --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --context-before 8 --context-after 20
        """;

    public const string DocumentSlices =
        """
        document-slices

        Purpose
          Fetch multiple code slices in one bridge request.

        Required
          --ranges-file <file>
          or
          --ranges <json>

        Recommendation
          Prefer --ranges-file to avoid shell escaping problems.

        Examples
          vs-ide-bridge document-slices --instance <instanceId> --ranges-file output\ranges.json
        """;

    public const string SearchSymbols =
        """
        search-symbols

        Purpose
          Search likely symbol definitions by name across the current solution or path filter.

        Required
          --query <symbol-name>

        Optional
          --kind function|class|struct|enum|namespace|interface|member|type|all
          --scope solution|project|document|open
          --project <name>
          --path <subdir>
          --max <n>
          --match-case

        Examples
          vs-ide-bridge search-symbols --instance <instanceId> --query propose_export_file_name_and_path --kind function
          vs-ide-bridge search-symbols --instance <instanceId> --query RunAsync --kind function --path src\VsIdeBridgeCli
        """;

    public const string GoToDefinition =
        """
        goto-definition

        Purpose
          Navigate to the definition of the symbol at a location.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>

        Examples
          vs-ide-bridge goto-definition --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string PeekDefinition =
        """
        peek-definition

        Purpose
          Return definition context for the symbol at a location without leaving the current source location selected.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>
          --context-lines <n>

        Examples
          vs-ide-bridge peek-definition --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string GoToImplementation =
        """
        goto-implementation

        Purpose
          Navigate to one implementation of the symbol at a location.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>

        Examples
          vs-ide-bridge goto-implementation --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string FindReferences =
        """
        find-references

        Purpose
          Run Visual Studio Find All References for the symbol at a location.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>
          --select-word
          --activate-window
          --timeout-ms <ms>

        Examples
          vs-ide-bridge find-references --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string CountReferences =
        """
        count-references

        Purpose
          Run Find All References and return exact count when Visual Studio exposes one.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>
          --select-word
          --activate-window
          --timeout-ms <ms>

        Result
          Returns countKnown=true with count when exact extraction succeeds.
          Otherwise returns countKnown=false with reason.

        Examples
          vs-ide-bridge count-references --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string CallHierarchy =
        """
        call-hierarchy

        Purpose
          Open Call Hierarchy for the symbol at a location.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>
          --select-word
          --activate-window
          --timeout-ms <ms>

        Examples
          vs-ide-bridge call-hierarchy --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string QuickInfo =
        """
        quick-info

        Purpose
          Get IntelliSense-style information for a symbol at a file, line, and column.

        Common usage
          --file <path> --line <n> --column <n>

        Optional
          --document <name>
          --context-lines <n>

        Examples
          vs-ide-bridge quick-info --instance <instanceId> --file C:\repo\src\foo.cpp --line 42 --column 13
        """;

    public const string DebugThreads =
        """
        debug-threads

        Purpose
          Capture debugger thread snapshot.

        Examples
          vs-ide-bridge debug-threads --instance <instanceId>
        """;

    public const string DebugStack =
        """
        debug-stack

        Purpose
          Capture stack frames for the current thread or a selected thread id.

        Optional
          --thread-id <id>
          --max-frames <n>

        Examples
          vs-ide-bridge debug-stack --instance <instanceId> --thread-id 1 --max-frames 50
        """;

    public const string DebugLocals =
        """
        debug-locals

        Purpose
          Capture local variables for the active stack frame in break mode.

        Optional
          --max <n>

        Examples
          vs-ide-bridge debug-locals --instance <instanceId> --max 100
        """;

    public const string DebugModules =
        """
        debug-modules

        Purpose
          Capture debugger module snapshot (best effort by debugger engine).

        Examples
          vs-ide-bridge debug-modules --instance <instanceId>
        """;

    public const string DebugWatch =
        """
        debug-watch

        Purpose
          Evaluate one watch expression in break mode.

        Required
          --expression <text>

        Optional
          --timeout-ms <ms>

        Examples
          vs-ide-bridge debug-watch --instance <instanceId> --expression count
        """;

    public const string DebugExceptions =
        """
        debug-exceptions

        Purpose
          Capture debugger exception settings/groups snapshot (best effort).

        Examples
          vs-ide-bridge debug-exceptions --instance <instanceId>
        """;

    public const string DiagnosticsSnapshot =
        """
        diagnostics-snapshot

        Purpose
          Capture IDE/debug/build state and current errors/warnings in one response.

        Optional
          --wait-for-intellisense
          --quick
          --max <n>
          --timeout-ms <ms>

        Examples
          vs-ide-bridge diagnostics-snapshot --instance <instanceId> --wait-for-intellisense --max 200
        """;

    public const string BuildConfigurations =
        """
        build-configurations

        Purpose
          List available solution build configurations and platforms.

        Examples
          vs-ide-bridge build-configurations --instance <instanceId>
        """;

    public const string SetBuildConfiguration =
        """
        set-build-configuration

        Purpose
          Activate one build configuration/platform pair.

        Required
          --configuration <name>

        Optional
          --platform <name>

        Examples
          vs-ide-bridge set-build-configuration --instance <instanceId> --configuration Debug --platform x64
        """;

    public const string FileSymbols =
        """
        file-symbols

        Purpose
          List symbols from one file, with optional kind filtering.

        Required
          --file <path>

        Optional
          --kind function|class|struct|enum|namespace|interface|member|type|all
          --max-depth <n>

        Examples
          vs-ide-bridge file-symbols --instance <instanceId> --file C:\repo\src\foo.cpp
          vs-ide-bridge file-symbols --instance <instanceId> --file C:\repo\src\foo.cpp --kind function
        """;

    public const string Close =
        """
        close

        Purpose
          Close Visual Studio gracefully using the DTE Quit API.

        Options
          --instance <id>    Target VS instance (required if multiple are running)

        Example
          vs-ide-bridge close --instance <instanceId>
        """;

    public const string Send =
        """
        send

        Purpose
          Send one bridge command directly.

        Required
          --command <simple-command-name>

        Optional
          --args <raw argument string>
          --wait-for-ready
          --request-id <id>

        Examples
          vs-ide-bridge send --instance <instanceId> --command state
          vs-ide-bridge send --instance <instanceId> --command find-files --args "--query PipeServerService.cs"
          vs-ide-bridge call --instance <instanceId> --command help
        """;

    public const string Batch =
        """
        batch

        Purpose
          Send multiple bridge commands in one request.

        Required
          --file <batch.json>

        Examples
          vs-ide-bridge batch --instance <instanceId> --file output\pipe-test-batch.json
          vs-ide-bridge batch --instance <instanceId> --file output\pipe-test-batch.json --stop-on-error
        """;

    public const string Request =
        """
        request

        Purpose
          Send raw JSON for advanced scenarios.

        Required
          exactly one of:
            --json <text>
            --json-file <file>

        Examples
          vs-ide-bridge request --instance <instanceId> --json "{ \"command\": \"state\" }"
          vs-ide-bridge request --instance <instanceId> --json-file output\pipe-test-batch.json
        """;

    public const string McpServer =
        """
        mcp-server

        Purpose
          Run a stdio MCP server over one live VS IDE Bridge instance.

        Optional
          --instance <instanceId>
          --pid <pid>
          --pipe <pipeName>
          --sln <hint>
          --discovery-mode json-only|hybrid|memory-first
          --emit-discovery-json true|false
          --tools-only

        Notes
          Resolves the selected bridge once and reuses that pipe for the MCP session.
          discovery-mode defaults to memory-first with JSON fallback compatibility.
          --tools-only advertises MCP tools capability only; resources/prompts methods remain callable.
          Use MCP tool `tool_help` to retrieve all MCP tools with schemas and examples.

        Example
          vs-ide-bridge mcp-server --instance <instanceId>
          vs-ide-bridge mcp-server --instance <instanceId> --tools-only
        """;
}

internal sealed class PipeArgsBuilder
{
    private readonly List<string> _tokens = [];

    public void AddRequired(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliException($"Missing required option --{name}.");
        }

        Add(name, value);
    }

    public void Add(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _tokens.Add($"--{name}");
        _tokens.Add(Escape(value));
    }

    public void AddFlag(string name, bool enabled)
    {
        if (enabled)
        {
            _tokens.Add($"--{name}");
        }
    }

    public string Build()
    {
        return string.Join(" ", _tokens);
    }

    private static string Escape(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || ch == '\\');
        if (!needsQuotes)
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"' || ch == '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        builder.Append('"');
        return builder.ToString();
    }
}

internal enum DiscoveryMode
{
    JsonOnly,
    Hybrid,
    MemoryFirst,
}

internal sealed class PipeDiscovery
{
    private const string MemoryMapName = @"Global\VsIdeBridge.Discovery.v1";
    private const string MemoryMutexName = @"Global\VsIdeBridge.Discovery.v1.mutex";
    private const int MemoryCapacityBytes = 1024 * 1024;

    public required string InstanceId { get; init; }
    public required string PipeName { get; init; }
    public required int ProcessId { get; init; }
    public required string SolutionPath { get; init; }
    public required string SolutionName { get; init; }
    public string? StartedAtUtc { get; init; }
    public required string DiscoveryFile { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
    public required string Source { get; init; }

    public static Task<IReadOnlyList<PipeDiscovery>> ListAsync(bool verbose)
    {
        return ListAsync(verbose, DiscoveryMode.MemoryFirst);
    }

    public static async Task<IReadOnlyList<PipeDiscovery>> ListAsync(bool verbose, DiscoveryMode discoveryMode)
    {
        var jsonInstances = discoveryMode == DiscoveryMode.MemoryFirst || discoveryMode == DiscoveryMode.Hybrid || discoveryMode == DiscoveryMode.JsonOnly
            ? await ListJsonAsync().ConfigureAwait(false)
            : [];

        if (discoveryMode == DiscoveryMode.JsonOnly)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"Found {jsonInstances.Count} live bridge instance(s) from json discovery.");
            }

            return jsonInstances;
        }

        var memoryInstances = ListMemoryAsync();
        var combined = discoveryMode switch
        {
            DiscoveryMode.MemoryFirst => MergeInstances(memoryInstances, jsonInstances, preferPrimary: true),
            DiscoveryMode.Hybrid => MergeInstances(memoryInstances, jsonInstances, preferPrimary: false),
            _ => jsonInstances,
        };

        if (verbose)
        {
            Console.Error.WriteLine(
                $"Found {combined.Count} live bridge instance(s) " +
                $"(memory={memoryInstances.Count}, json={jsonInstances.Count}, mode={discoveryMode.ToString().ToLowerInvariant()}).");
        }

        return combined;
    }

    private static async Task<IReadOnlyList<PipeDiscovery>> ListJsonAsync()
    {
        var temp = Environment.GetEnvironmentVariable("TEMP")
            ?? Environment.GetEnvironmentVariable("TMP")
            ?? Path.GetTempPath();
        var directory = Path.Combine(temp, "vs-ide-bridge", "pipes");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory.GetFiles(directory, "bridge-*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => ParsePid(file.Name))
            .ToArray();
        if (files.Length == 0)
        {
            return [];
        }

        var instances = new List<PipeDiscovery>();
        foreach (var file in files)
        {
            var instance = await TryLoadAsync(file.FullName).ConfigureAwait(false);
            if (instance is null)
            {
                continue;
            }

            instances.Add(instance);
        }

        return instances;
    }

    public static IEnumerable<PipeDiscovery> Filter(IEnumerable<PipeDiscovery> instances, BridgeInstanceSelector selector)
    {
        foreach (var instance in instances)
        {
            if (!string.IsNullOrWhiteSpace(selector.InstanceId) &&
                !string.Equals(instance.InstanceId, selector.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (selector.ProcessId is int processId && instance.ProcessId != processId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selector.PipeName) &&
                !string.Equals(instance.PipeName, selector.PipeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selector.SolutionHint))
            {
                var matched = instance.SolutionPath.Contains(selector.SolutionHint, StringComparison.OrdinalIgnoreCase)
                    || instance.SolutionName.Contains(selector.SolutionHint, StringComparison.OrdinalIgnoreCase);
                if (!matched)
                {
                    continue;
                }
            }

            yield return instance;
        }
    }

    public static Task<PipeDiscovery> SelectAsync(BridgeInstanceSelector selector, bool verbose)
    {
        return SelectAsync(selector, verbose, DiscoveryMode.MemoryFirst);
    }

    public static async Task<PipeDiscovery> SelectAsync(BridgeInstanceSelector selector, bool verbose, DiscoveryMode discoveryMode)
    {
        var instances = await ListAsync(verbose, discoveryMode).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            throw new CliException(
                "No live VS IDE Bridge instance found. " +
                "Open Visual Studio with the VS IDE Bridge extension installed, then run 'vs-ide-bridge instances'.");
        }

        var matches = Filter(instances, selector).ToArray();
        if (matches.Length == 1)
        {
            if (verbose)
            {
                Console.Error.WriteLine(
                    $"Using instance '{matches[0].InstanceId}' on pipe '{matches[0].PipeName}' " +
                    $"(source={matches[0].Source}).");
            }

            return matches[0];
        }

        if (matches.Length == 0)
        {
            throw new CliException(selector.HasAny
                ? $"No live VS IDE Bridge instance matched {selector.Describe()}. Run 'vs-ide-bridge instances --format summary' to inspect the live instances."
                : "Multiple live VS IDE Bridge instances are available. Run 'vs-ide-bridge instances --format summary' and then rerun with --instance <instanceId>.");
        }

        throw new CliException(
            $"Multiple live VS IDE Bridge instances matched {selector.Describe()}. " +
            $"Use 'vs-ide-bridge instances --format summary' to choose one.{Environment.NewLine}" +
            InstanceFormatter.Format(matches, "summary"));
    }

    private static IReadOnlyList<PipeDiscovery> ListMemoryAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            using var mutex = new System.Threading.Mutex(false, MemoryMutexName);
            var hasLock = false;
            try
            {
                hasLock = mutex.WaitOne(TimeSpan.FromSeconds(2));
                if (!hasLock)
                {
                    return [];
                }

                using var map = MemoryMappedFile.OpenExisting(MemoryMapName, MemoryMappedFileRights.Read);
                using var view = map.CreateViewStream(0, MemoryCapacityBytes, MemoryMappedFileAccess.Read);
                var lenBuffer = new byte[4];
                var bytesRead = view.Read(lenBuffer, 0, lenBuffer.Length);
                if (bytesRead < lenBuffer.Length)
                {
                    return [];
                }

                var payloadLength = BitConverter.ToInt32(lenBuffer, 0);
                if (payloadLength <= 0 || payloadLength > MemoryCapacityBytes - 4)
                {
                    return [];
                }

                var payload = new byte[payloadLength];
                bytesRead = view.Read(payload, 0, payload.Length);
                if (bytesRead != payloadLength)
                {
                    return [];
                }

                var root = JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject;
                var items = root?["items"] as JsonArray;
                if (items is null)
                {
                    return [];
                }

                var instances = new List<PipeDiscovery>();
                foreach (var entry in items.OfType<JsonObject>())
                {
                    var updatedAt = entry["updatedAtUtc"]?.GetValue<string>();
                    var updatedAtUtc = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(updatedAt) &&
                        DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        updatedAtUtc = parsed.UtcDateTime;
                    }

                    var instance = TryCreateFromNode(
                        entry,
                        "memory://global/VsIdeBridge.Discovery.v1",
                        updatedAtUtc,
                        "memory");
                    if (instance is not null)
                    {
                        instances.Add(instance);
                    }
                }

                return instances;
            }
            finally
            {
                if (hasLock)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<PipeDiscovery> MergeInstances(
        IReadOnlyCollection<PipeDiscovery> primary,
        IReadOnlyCollection<PipeDiscovery> secondary,
        bool preferPrimary)
    {
        var merged = new Dictionary<string, PipeDiscovery>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in primary)
        {
            merged[BuildInstanceKey(item)] = item;
        }

        foreach (var item in secondary)
        {
            var key = BuildInstanceKey(item);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = item;
                continue;
            }

            if (preferPrimary)
            {
                continue;
            }

            if (item.LastWriteTimeUtc > existing.LastWriteTimeUtc)
            {
                merged[key] = item;
            }
        }

        return [.. merged.Values.OrderByDescending(item => item.LastWriteTimeUtc)];
    }

    private static string BuildInstanceKey(PipeDiscovery item)
    {
        return $"{item.InstanceId}|{item.ProcessId}|{item.PipeName}";
    }

    private static int ParsePid(string fileName)
    {
        var start = fileName.IndexOf('-');
        var end = fileName.LastIndexOf('.');
        if (start < 0 || end <= start)
        {
            return -1;
        }

        return int.TryParse(fileName.AsSpan(start + 1, end - start - 1), out var pid) ? pid : -1;
    }

    private static async Task<PipeDiscovery?> TryLoadAsync(string path)
    {
        JsonObject? info;
        try
        {
            info = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false)) as JsonObject;
        }
        catch
        {
            return null;
        }

        var file = new FileInfo(path);
        return TryCreateFromNode(info, file.FullName, file.LastWriteTimeUtc, "json");
    }

    private static PipeDiscovery? TryCreateFromNode(JsonObject? info, string discoveryFile, DateTime lastWriteTimeUtc, string source)
    {
        var pipeName = info?["pipeName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return null;
        }

        var processId = info?["pid"]?.GetValue<int?>() ?? ParsePid(Path.GetFileName(discoveryFile));
        if (processId <= 0)
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var startedAtUtc = info?["startedAtUtc"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(startedAtUtc) && !IsSameProcessStart(process, startedAtUtc))
        {
            return null;
        }

        var solutionPath = info?["solutionPath"]?.GetValue<string>() ?? string.Empty;
        var solutionName = info?["solutionName"]?.GetValue<string>()
            ?? (string.IsNullOrWhiteSpace(solutionPath) ? string.Empty : Path.GetFileName(solutionPath));
        var instanceId = info?["instanceId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instanceId = $"vs18-{processId}";
        }

        return new PipeDiscovery
        {
            InstanceId = instanceId,
            PipeName = pipeName,
            ProcessId = processId,
            SolutionPath = solutionPath,
            SolutionName = solutionName,
            StartedAtUtc = startedAtUtc,
            DiscoveryFile = discoveryFile,
            LastWriteTimeUtc = lastWriteTimeUtc,
            Source = source,
        };
    }

    private static bool IsSameProcessStart(Process process, string startedAtUtc)
    {
        if (!DateTimeOffset.TryParse(
                startedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return true;
        }

        try
        {
            var processStart = process.StartTime.ToUniversalTime();
            return Math.Abs((processStart - parsed.UtcDateTime).TotalSeconds) <= 2;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class BridgeInstanceSelector
{
    public string? InstanceId { get; init; }
    public int? ProcessId { get; init; }
    public string? PipeName { get; init; }
    public string? SolutionHint { get; init; }

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(InstanceId)
        || ProcessId is not null
        || !string.IsNullOrWhiteSpace(PipeName)
        || !string.IsNullOrWhiteSpace(SolutionHint);

    public static BridgeInstanceSelector FromOptions(CliOptions options)
    {
        return new BridgeInstanceSelector
        {
            InstanceId = options.GetValue("instance"),
            ProcessId = options.GetNullableInt32("pid"),
            PipeName = options.GetValue("pipe"),
            SolutionHint = options.GetValue("sln"),
        };
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(InstanceId))
        {
            parts.Add($"--instance {InstanceId}");
        }

        if (ProcessId is int processId)
        {
            parts.Add($"--pid {processId}");
        }

        if (!string.IsNullOrWhiteSpace(PipeName))
        {
            parts.Add($"--pipe {PipeName}");
        }

        if (!string.IsNullOrWhiteSpace(SolutionHint))
        {
            parts.Add($"--sln {SolutionHint}");
        }

        return parts.Count == 0 ? "the current selector" : string.Join(" ", parts);
    }
}

internal static class InstanceFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string Format(IReadOnlyCollection<PipeDiscovery> instances, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "keyvalue" => FormatKeyValue(instances),
            "json" => FormatJson(instances),
            _ => FormatSummary(instances),
        };
    }

    public static string FormatSingle(PipeDiscovery instance, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "keyvalue" => FormatSingleKeyValue(instance),
            "json" => FormatSingleJson(instance),
            _ => FormatSingleSummary(instance),
        };
    }

    private static string FormatSummary(IReadOnlyCollection<PipeDiscovery> instances)
    {
        var lines = new List<string> { $"[OK] {instances.Count} live bridge instance(s)." };
        foreach (var instance in instances.OrderByDescending(item => item.LastWriteTimeUtc))
        {
            var solution = string.IsNullOrWhiteSpace(instance.SolutionPath) ? "<no solution>" : instance.SolutionPath;
            lines.Add($"  {instance.InstanceId} pid={instance.ProcessId} pipe={instance.PipeName} source={instance.Source} solution={solution}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatKeyValue(IReadOnlyCollection<PipeDiscovery> instances)
    {
        var lines = new List<string> { $"count={instances.Count}" };
        var index = 0;
        foreach (var instance in instances.OrderByDescending(item => item.LastWriteTimeUtc))
        {
            lines.Add($"instances[{index}].instanceId={instance.InstanceId}");
            lines.Add($"instances[{index}].pid={instance.ProcessId}");
            lines.Add($"instances[{index}].pipeName={instance.PipeName}");
            lines.Add($"instances[{index}].solutionPath={instance.SolutionPath}");
            lines.Add($"instances[{index}].solutionName={instance.SolutionName}");
            lines.Add($"instances[{index}].startedAtUtc={instance.StartedAtUtc}");
            lines.Add($"instances[{index}].source={instance.Source}");
            index++;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSingleSummary(PipeDiscovery instance)
    {
        var solution = string.IsNullOrWhiteSpace(instance.SolutionPath) ? "<no solution>" : instance.SolutionPath;
        return $"[OK] current instance={instance.InstanceId} pid={instance.ProcessId} pipe={instance.PipeName} source={instance.Source} solution={solution}";
    }

    private static string FormatSingleKeyValue(PipeDiscovery instance)
    {
        var lines = new List<string>
        {
            $"instanceId={instance.InstanceId}",
            $"pid={instance.ProcessId}",
            $"pipeName={instance.PipeName}",
            $"solutionPath={instance.SolutionPath}",
            $"solutionName={instance.SolutionName}",
            $"startedAtUtc={instance.StartedAtUtc}",
            $"source={instance.Source}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatJson(IReadOnlyCollection<PipeDiscovery> instances)
    {
        JsonArray array =
        [
            .. instances
                .OrderByDescending(item => item.LastWriteTimeUtc)
                .Select(instance => new JsonObject
                {
                    ["instanceId"] = instance.InstanceId,
                    ["pid"] = instance.ProcessId,
                    ["pipeName"] = instance.PipeName,
                    ["solutionPath"] = instance.SolutionPath,
                    ["solutionName"] = instance.SolutionName,
                    ["startedAtUtc"] = instance.StartedAtUtc,
                    ["source"] = instance.Source,
                    ["discoveryFile"] = instance.DiscoveryFile,
                    ["lastWriteTimeUtc"] = instance.LastWriteTimeUtc.ToString("O"),
                }),
        ];
        return array.ToJsonString(JsonOptions);
    }

    private static string FormatSingleJson(PipeDiscovery instance)
    {
        var obj = new JsonObject
        {
            ["instanceId"] = instance.InstanceId,
            ["pid"] = instance.ProcessId,
            ["pipeName"] = instance.PipeName,
            ["solutionPath"] = instance.SolutionPath,
            ["solutionName"] = instance.SolutionName,
            ["startedAtUtc"] = instance.StartedAtUtc,
            ["source"] = instance.Source,
            ["discoveryFile"] = instance.DiscoveryFile,
            ["lastWriteTimeUtc"] = instance.LastWriteTimeUtc.ToString("O"),
        };
        return obj.ToJsonString(JsonOptions);
    }
}

internal sealed class PipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly int _timeoutMs;
    private readonly FileStream _requestGate;

    public PipeClient(string pipeName, int timeoutMs)
    {
        _timeoutMs = Math.Max(1_000, timeoutMs);
        _requestGate = AcquireRequestGate(pipeName, _timeoutMs);

        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(_timeoutMs);
        _pipe.ConnectAsync(cts.Token).GetAwaiter().GetResult();
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public async Task<JsonObject> SendAsync(JsonObject payload)
    {
        using var cts = new CancellationTokenSource(_timeoutMs);
        var payloadJson = payload.ToJsonString();

        try
        {
            await _writer.WriteLineAsync(payloadJson.AsMemory(), cts.Token).ConfigureAwait(false);
            var line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new CliException("The bridge pipe returned an empty response.");
            }

            return JsonNode.Parse(line) as JsonObject
                   ?? throw new CliException("The bridge pipe returned malformed JSON.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Timed out waiting for bridge response after {_timeoutMs} ms. Visual Studio may be blocked by a modal dialog.");
        }
    }

    private static string BuildGateName(string pipeName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pipeName ?? string.Empty));
        return Convert.ToHexString(hash);
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
        _requestGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static FileStream AcquireRequestGate(string pipeName, int timeoutMs)
    {
        var lockDirectory = Path.Combine(Path.GetTempPath(), "vs-ide-bridge", "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockFile = Path.Combine(lockDirectory, $"{BuildGateName(pipeName)}.lock");
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }

        throw new CliException(
            $"Bridge pipe '{pipeName}' is busy with another request. Avoid parallel calls and retry.");
    }
}

internal static class ResponseFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static bool IsSuccess(JsonObject response)
    {
        return response["Success"]?.GetValue<bool>()
            ?? response["success"]?.GetValue<bool>()
            ?? false;
    }

    public static string Format(JsonObject response, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "summary" => FormatSummary(response),
            "keyvalue" => FormatKeyValue(response),
            _ => response.ToJsonString(JsonOptions),
        };
    }

    private static string FormatSummary(JsonObject response)
    {
        var success = IsSuccess(response);
        var summary = GetString(response, "Summary") ?? string.Empty;
        var lines = new List<string> { $"[{(success ? "OK" : "FAIL")}] {summary}" };

        if (response["Data"] is JsonObject data)
        {
            if (!success)
            {
                if (data["state"] is JsonObject state)
                {
                    var activeDocument = state["activeDocument"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(activeDocument))
                    {
                        lines.Add($"  Active document: {activeDocument}");
                    }
                }

                if (data["openTabs"] is JsonObject openTabs &&
                    openTabs["items"] is JsonArray tabItems &&
                    tabItems.Count > 0)
                {
                    lines.Add($"  Open tabs ({tabItems.Count}):");
                    foreach (var tab in tabItems.OfType<JsonObject>().Take(8))
                    {
                        var name = tab["name"]?.GetValue<string>() ?? "(unknown)";
                        var prefix = tab["isActive"]?.GetValue<bool>() == true ? "*" : "-";
                        lines.Add($"  {prefix} {name}");
                    }
                }

                if (data["errorList"] is JsonObject errorList &&
                    errorList["severityCounts"] is JsonObject severityCounts)
                {
                    var errors = severityCounts["Error"]?.GetValue<int>() ?? 0;
                    var warnings = severityCounts["Warning"]?.GetValue<int>() ?? 0;
                    var messages = severityCounts["Message"]?.GetValue<int>() ?? 0;
                    lines.Add($"  Error List: {errors} error(s), {warnings} warning(s), {messages} message(s)");
                }
            }

            if (data["commands"] is JsonArray commands)
            {
                lines.Add($"  Commands ({commands.Count}):");
                foreach (var command in commands)
                {
                    var commandName = command?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(commandName))
                    {
                        lines.Add($"  {commandName}");
                    }
                }
            }

            if (data["results"] is JsonArray results)
            {
                foreach (var item in results.OfType<JsonObject>())
                {
                    var itemSuccess = item["success"]?.GetValue<bool>() ?? false;
                    var itemId = item["id"]?.GetValue<string>();
                    var itemCommand = item["command"]?.GetValue<string>() ?? string.Empty;
                    var itemSummary = item["summary"]?.GetValue<string>() ?? string.Empty;
                    var prefix = itemSuccess ? "OK" : "FAIL";
                    var label = string.IsNullOrWhiteSpace(itemId) ? itemCommand : $"{itemId} {itemCommand}";
                    lines.Add($"  [{prefix}] {label} - {itemSummary}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatKeyValue(JsonObject response)
    {
        var lines = new List<string>();
        foreach (var pair in response)
        {
            if (pair.Value is JsonObject obj && string.Equals(pair.Key, "Data", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in obj)
                {
                    lines.Add($"data.{child.Key}={child.Value}");
                }
            }
            else
            {
                lines.Add($"{pair.Key}={pair.Value}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string? GetString(JsonObject response, string key)
    {
        return response[key]?.GetValue<string>()
            ?? response[key.ToLowerInvariant()]?.GetValue<string>();
    }
}

internal static class ParseFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetDefaultFormat(JsonNode? node, string? select)
    {
        if (string.IsNullOrWhiteSpace(select))
        {
            return "json";
        }

        return node is JsonObject or JsonArray ? "json" : "value";
    }

    public static string Format(JsonNode? node, string format, string? select)
    {
        return format.ToLowerInvariant() switch
        {
            "summary" => FormatSummary(node, select),
            "value" => FormatValue(node),
            "lines" => FormatLines(node),
            "keyvalue" => FormatKeyValue(node),
            _ => FormatJson(node),
        };
    }

    private static string FormatSummary(JsonNode? node, string? select)
    {
        var path = string.IsNullOrWhiteSpace(select) ? "/" : select;
        var kind = node switch
        {
            null => "null",
            JsonObject => "object",
            JsonArray array => $"array[{array.Count}]",
            JsonValue => "value",
            _ => "node",
        };

        var preview = node is JsonObject or JsonArray ? string.Empty : $" {FormatValue(node)}";
        return $"[OK] {path} -> {kind}{preview}";
    }

    private static string FormatJson(JsonNode? node)
    {
        return node?.ToJsonString(JsonOptions) ?? "null";
    }

    private static string FormatValue(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonValue)
        {
            return node.ToJsonString().Trim('"');
        }

        return node.ToJsonString();
    }

    private static string FormatLines(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return string.Join(Environment.NewLine, array.Select(FormatValue));
        }

        if (node is JsonObject obj)
        {
            return string.Join(
                Environment.NewLine,
                obj.Select(pair => $"{pair.Key}={FormatValue(pair.Value)}"));
        }

        return FormatValue(node);
    }

    private static string FormatKeyValue(JsonNode? node)
    {
        var lines = new List<string>();
        Flatten(lines, node, string.Empty);
        return lines.Count == 0 ? "/=null" : string.Join(Environment.NewLine, lines);
    }

    private static void Flatten(List<string> lines, JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    Flatten(lines, pair.Value, $"{path}/{pair.Key}");
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Flatten(lines, array[i], $"{path}/{i}");
                }

                break;
            default:
                lines.Add($"{(string.IsNullOrWhiteSpace(path) ? "/" : path)}={FormatValue(node)}");
                break;
        }
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"Unexpected argument '{token}'.");
            }

            var name = token[2..];

            // Support --key=value syntax. This is required when the value itself starts with
            // '--' (e.g. --args="--command foo"), because the lookahead heuristic below would
            // otherwise treat '--args' as a boolean flag when the next token begins with '--'.
            var eqIndex = name.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex >= 0)
            {
                options._values[name[..eqIndex]] = name[(eqIndex + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[name] = args[++i];
            }
            else
            {
                options._flags.Add(name);
            }
        }

        return options;
    }

    public string? GetValue(string name)
    {
        return _values.TryGetValue(name, out var value) ? value : null;
    }

    public string GetRequiredValue(string name)
    {
        return GetValue(name) ?? throw new CliException($"Missing required option --{name}.");
    }

    public bool GetFlag(string name)
    {
        return _flags.Contains(name);
    }

    public bool GetBoolean(string name, bool defaultValue)
    {
        var value = GetValue(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new CliException($"Option --{name} must be true or false.");
    }

    public int GetInt32(string name, int defaultValue)
    {
        var value = GetValue(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : int.Parse(value);
    }

    public int? GetNullableInt32(string name)
    {
        var value = GetValue(name);
        return string.IsNullOrWhiteSpace(value) ? null : int.Parse(value);
    }
}

internal sealed class CliException(string message) : Exception(message);
