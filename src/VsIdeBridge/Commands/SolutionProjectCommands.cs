using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static class SolutionProjectCommands
{
    private const string ProjectNotFoundCode = "project_not_found";
    private const string SolutionNotOpenCode = "solution_not_open";
    private const string UnsupportedProjectTypeCode = "unsupported_project_type";
    private const string UniqueNamePropertyName = "uniqueName";
    private const string FrameworkOrigin = "framework";
    private static readonly string[] PreferredOutputExtensions = [".vsix", ".exe", ".dll", ".winmd"];

    private static void EnsureSolutionOpen(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (dte.Solution?.IsOpen != true)
            throw new CommandErrorException(SolutionNotOpenCode, "No solution is open.");
    }

    private static Project? FindProject(DTE2 dte, string query)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var p in EnumerateAllProjects(dte))
        {
            if (string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.UniqueName, query, StringComparison.OrdinalIgnoreCase) ||
                (p.FullName is { Length: > 0 } && PathNormalization.AreEquivalent(p.FullName, query)))
            {
                return p;
            }
        }
        return null;
    }

    private static CommandErrorException CreateProjectNotFound(string projectQuery)
        => new(ProjectNotFoundCode, $"Project not found: {projectQuery}");

    private static IEnumerable<Project> EnumerateAllProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (Project p in dte.Solution.Projects)
        {
            foreach (var proj in EnumerateProjectTree(p))
            {
                yield return proj;
            }
        }
    }

    private static IEnumerable<Project> EnumerateProjectTree(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject is { } sub)
                {
                    foreach (var p in EnumerateProjectTree(sub))
                    {
                        yield return p;
                    }
                }
            }
        }
        else
        {
            yield return project;
        }
    }

    private static IReadOnlyList<string> GetStartupProjects(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (dte.Solution.SolutionBuild.StartupProjects is object[] arr)
                return [.. arr.OfType<string>()];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // StartupProjects throws when no startup project is configured; return empty list.
        }
        return [];
    }

    private static JObject ProjectToJson(Project p, IReadOnlyCollection<string> startupProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var uniqueName = p.UniqueName;
        return new JObject
        {
            ["name"] = p.Name,
            [UniqueNamePropertyName] = uniqueName,
            ["path"] = p.FullName,
            ["kind"] = p.Kind,
            ["isStartup"] = startupProjects.Any(s =>
                string.Equals(s, uniqueName, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static IEnumerable<ProjectItem> EnumerateProjectItems(ProjectItems? items)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (items is null)
        {
            yield break;
        }

        foreach (ProjectItem item in items)
        {
            yield return item;

            foreach (var child in EnumerateProjectItems(item.ProjectItems))
            {
                yield return child;
            }
        }
    }

    private static string[] GetProjectItemPaths(ProjectItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (item.FileCount <= 0)
        {
            return [];
        }

        var paths = new List<string>();
        for (var index = 1; index <= item.FileCount; index++)
        {
            try
            {
                var candidate = item.FileNames[(short)index];
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    paths.Add(PathNormalization.NormalizeFilePath(candidate));
                }
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Unable to read project item path '{item.Name}' index {index}: {ex.Message}");
            }
        }

        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static JToken? ToJsonToken(object? value)
    {
        return value switch
        {
            null => JValue.CreateNull(),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => JToken.FromObject(value),
            DateTime dateTime => dateTime.ToString("O"),
            Array array => new JArray(array.Cast<object?>().Select(ToJsonToken)),
            _ => value.ToString() is { Length: > 0 } text ? text : null,
        };
    }

    private static JToken? TryGetPropertyValue(Properties? properties, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (properties is null)
        {
            return null;
        }

        try
        {
            return ToJsonToken(properties.Item(name)?.Value);
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Project property '{name}' is unavailable: {ex.Message}");
            return null;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Project property '{name}' could not be read: {ex.Message}");
            return null;
        }
        catch (NotImplementedException ex)
        {
            Debug.WriteLine($"Project property '{name}' is not implemented by this project type: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Project property '{name}' is not supported by this project type: {ex.Message}");
            return null;
        }
    }

    private static JToken? TryGetNormalizedPropertyValue(Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return NormalizeProjectPropertyValue(project, name, TryGetPropertyValue(project.Properties, name));
    }

    private static string? TryGetPropertyString(Properties? properties, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var value = TryGetPropertyValue(properties, name);
        return value?.Type == JTokenType.Null
            ? null
            : value?.ToString();
    }

    private static IReadOnlyList<(string Name, JToken Value)> EnumerateProjectProperties(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var properties = project.Properties;
        if (properties is null)
        {
            return [];
        }

        var values = new List<(string Name, JToken Value)>();

        foreach (Property property in properties)
        {
            string? name = null;
            try
            {
                name = property.Name;
                var value = NormalizeProjectPropertyValue(project, name, ToJsonToken(property.Value));
                if (!string.IsNullOrWhiteSpace(name) && value is not null)
                {
                    values.Add((name, value));
                }
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}': {ex.Message}");
            }
            catch (NotImplementedException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}' because it is not implemented by this project type: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                Debug.WriteLine($"Skipping project property '{name ?? "<unknown>"}' because it is not supported by this project type: {ex.Message}");
            }
        }

        return values;
    }

    private static JToken? NormalizeProjectPropertyValue(Project project, string name, JToken? value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (value is null)
        {
            return null;
        }

        return string.Equals(name, "TargetFramework", StringComparison.OrdinalIgnoreCase)
            ? NormalizeTargetFrameworkValue(project, value)
            : value;
    }

    private static JToken NormalizeTargetFrameworkValue(Project project, JToken value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (value.Type == JTokenType.String)
        {
            var current = value.ToString();
            if (!string.IsNullOrWhiteSpace(current) && !int.TryParse(current, out _))
            {
                return value;
            }
        }

        var targetFrameworks = TryGetPropertyString(project.Properties, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return targetFrameworks;
        }

        var moniker = TryGetPropertyString(project.Properties, "TargetFrameworkMoniker")
            ?? TryGetPropertyString(project.Properties, "TargetFrameworkMonikers");

        var friendlyTargetFramework = TryConvertFrameworkMonikerToTfm(
            moniker,
            TryGetPropertyString(project.Properties, "TargetPlatformIdentifier"),
            TryGetPropertyString(project.Properties, "TargetPlatformVersion"));

        return string.IsNullOrWhiteSpace(friendlyTargetFramework)
            ? value
            : new JValue(friendlyTargetFramework);
    }

    private static string? TryConvertFrameworkMonikerToTfm(string? moniker, string? targetPlatformIdentifier, string? targetPlatformVersion)
    {
        if (string.IsNullOrWhiteSpace(moniker))
        {
            return null;
        }

        var monikerText = moniker!;
        var primaryMoniker = monikerText
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(static value => value.Length > 0);

        if (string.IsNullOrWhiteSpace(primaryMoniker))
        {
            return null;
        }

        const string NetCoreAppPrefix = ".NETCoreApp,Version=v";
        const string NetStandardPrefix = ".NETStandard,Version=v";
        const string NetFrameworkPrefix = ".NETFramework,Version=v";

        string? baseTfm = primaryMoniker switch
        {
            var value when value.StartsWith(NetCoreAppPrefix, StringComparison.OrdinalIgnoreCase)
                => "net" + value.Substring(NetCoreAppPrefix.Length),
            var value when value.StartsWith(NetStandardPrefix, StringComparison.OrdinalIgnoreCase)
                => "netstandard" + value.Substring(NetStandardPrefix.Length),
            var value when value.StartsWith(NetFrameworkPrefix, StringComparison.OrdinalIgnoreCase)
                => "net" + value.Substring(NetFrameworkPrefix.Length).Replace(".", string.Empty),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(baseTfm))
        {
            return null;
        }

        if (!string.Equals(targetPlatformIdentifier, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return baseTfm;
        }

        return string.IsNullOrWhiteSpace(targetPlatformVersion)
            ? baseTfm + "-windows"
            : baseTfm + "-windows" + targetPlatformVersion;
    }

    private static IEnumerable<Configuration> EnumerateProjectConfigurations(ConfigurationManager? configurationManager)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (configurationManager is null)
        {
            yield break;
        }

        foreach (Configuration configuration in configurationManager)
        {
            yield return configuration;
        }
    }

    private static string[] SplitRequestedNames(string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return [];
        }

        return [.. names!
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool MatchesPathFilter(IEnumerable<string> paths, string? pathFilter, string solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(pathFilter))
        {
            return true;
        }

        var normalizedPaths = paths.ToArray();
        if (normalizedPaths.Length == 0)
        {
            return false;
        }

        var filter = (pathFilter ?? string.Empty).Replace('/', '\\').Trim();
        if (Path.IsPathRooted(filter))
        {
            var normalizedFilter = PathNormalization.NormalizeFilePath(filter);
            return normalizedPaths.Any(path =>
                PathNormalization.AreEquivalent(path, normalizedFilter) ||
                path.StartsWith(normalizedFilter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        var rootedFilter = PathNormalization.NormalizeFilePath(Path.Combine(solutionDirectory, filter));
        if (normalizedPaths.Any(path =>
            PathNormalization.AreEquivalent(path, rootedFilter) ||
            path.StartsWith(rootedFilter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var normalizedFragment = filter.Trim('\\');
        return normalizedPaths.Any(path =>
            path.IndexOf(normalizedFragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static JObject ProjectItemToJson(ProjectItem item, string[] paths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = item.Name,
            ["path"] = paths.FirstOrDefault(),
            ["paths"] = new JArray(paths),
            ["kind"] = item.Kind ?? string.Empty,
            ["itemType"] = TryGetPropertyString(item.Properties, "ItemType") ?? string.Empty,
            ["subType"] = TryGetPropertyString(item.Properties, "SubType") ?? string.Empty,
            ["fileCount"] = item.FileCount,
        };
    }

    private static JObject ProjectSummaryToJson(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = project.Name,
            [UniqueNamePropertyName] = project.UniqueName,
            ["path"] = project.FullName,
            ["kind"] = project.Kind,
        };
    }

    private static string? GetConfigurationMoniker(Configuration? configuration)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (configuration is null)
        {
            return null;
        }

        return $"{configuration.ConfigurationName}|{configuration.PlatformName}";
    }

    private static JObject ProjectConfigurationToJson(Configuration configuration, Configuration? activeConfiguration)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var moniker = GetConfigurationMoniker(configuration);
        return new JObject
        {
            ["name"] = moniker,
            ["configurationName"] = configuration.ConfigurationName,
            ["platformName"] = configuration.PlatformName,
            ["isActive"] = string.Equals(moniker, GetConfigurationMoniker(activeConfiguration), StringComparison.OrdinalIgnoreCase),
        };
    }

    private static ConfigurationManager? TryGetConfigurationManager(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.ConfigurationManager;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Project '{project.Name}' does not expose configurations: {ex.Message}");
            return null;
        }
    }

    private static Configuration? TryGetActiveProjectConfiguration(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var configurationManager = TryGetConfigurationManager(project);
        if (configurationManager is null)
        {
            return null;
        }

        try
        {
            return configurationManager.ActiveConfiguration;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Unable to read active configuration for '{project.Name}': {ex.Message}");
            return null;
        }
    }

    private static string? GetPrimaryValue(string? value)
    {
        return NormalizeXmlValue(
            value?
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.Length > 0));
    }

    private static string? GetNormalizedProjectPropertyString(Project project, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var token = TryGetNormalizedPropertyValue(project, name);
        return token is null || token.Type == JTokenType.Null
            ? null
            : NormalizeXmlValue(token.ToString());
    }

    private static string GetNormalizedOutputType(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var raw = GetNormalizedProjectPropertyString(project, "OutputType");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Unknown";
        }

        if (!int.TryParse(raw, out var numericValue))
        {
            return raw!;
        }

        return numericValue switch
        {
            2 => "Library",
            0 or 1 => "Exe",
            _ => raw!,
        };
    }

    private static string[] GetOutputDirectoryCandidates(string projectDirectory, string configurationName, string? platformName, string? targetFramework)
    {
        var candidates = new List<string>();

        void AddCandidate(params string[] parts)
        {
            var filtered = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
            if (filtered.Length == 0)
            {
                return;
            }

            var candidate = PathNormalization.NormalizeFilePath(Path.Combine(filtered));
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        AddCandidate(projectDirectory, "bin", configurationName, targetFramework ?? string.Empty);
        AddCandidate(projectDirectory, "bin", configurationName);

        if (!string.IsNullOrWhiteSpace(platformName) && !string.Equals(platformName, "Any CPU", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(projectDirectory, "bin", platformName!, configurationName, targetFramework ?? string.Empty);
            AddCandidate(projectDirectory, "bin", platformName!, configurationName);
        }

        return [.. candidates];
    }

    private static string? FindPrimaryOutputPath(IEnumerable<string> candidateDirectories, string assemblyName)
    {
        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var extension in PreferredOutputExtensions)
            {
                var candidate = Path.Combine(directory, assemblyName + extension);
                if (File.Exists(candidate))
                {
                    return PathNormalization.NormalizeFilePath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] EnumerateOutputArtifacts(string? outputDirectory, string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(outputDirectory)
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(PathNormalization.NormalizeFilePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetFallbackTargetExtension(Project project, string normalizedOutputType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var projectDirectory = Path.GetDirectoryName(project.FullName) ?? string.Empty;
        if (File.Exists(Path.Combine(projectDirectory, "source.extension.vsixmanifest")))
        {
            return ".vsix";
        }

        return string.Equals(normalizedOutputType, "Library", StringComparison.OrdinalIgnoreCase)
            ? ".dll"
            : ".exe";
    }

    private static JObject ProjectOutputToJson(
        Project project,
        string configurationName,
        string? platformName,
        string? targetFramework,
        string assemblyName,
        string normalizedOutputType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var projectDirectory = Path.GetDirectoryName(project.FullName) ?? string.Empty;
        var candidateDirectories = GetOutputDirectoryCandidates(projectDirectory, configurationName, platformName, targetFramework);
        var primaryOutputPath = FindPrimaryOutputPath(candidateDirectories, assemblyName);
        var outputDirectory = primaryOutputPath is null
            ? candidateDirectories.FirstOrDefault()
            : Path.GetDirectoryName(primaryOutputPath);
        var targetExtension = primaryOutputPath is null
            ? GetFallbackTargetExtension(project, normalizedOutputType)
            : Path.GetExtension(primaryOutputPath);
        var targetName = primaryOutputPath is null
            ? assemblyName
            : Path.GetFileNameWithoutExtension(primaryOutputPath);
        var targetFileName = primaryOutputPath is null
            ? targetName + targetExtension
            : Path.GetFileName(primaryOutputPath);

        return new JObject
        {
            ["project"] = project.Name,
            [UniqueNamePropertyName] = project.UniqueName,
            ["configuration"] = configurationName,
            ["platform"] = platformName,
            ["targetFramework"] = targetFramework,
            ["assemblyName"] = assemblyName,
            ["outputType"] = normalizedOutputType,
            ["outputDirectory"] = outputDirectory,
            ["targetName"] = targetName,
            ["targetExtension"] = targetExtension,
            ["targetFileName"] = targetFileName,
            ["targetPath"] = primaryOutputPath,
            ["exists"] = primaryOutputPath is not null && File.Exists(primaryOutputPath),
            ["searchedDirectories"] = new JArray(candidateDirectories),
            ["artifacts"] = new JArray(EnumerateOutputArtifacts(outputDirectory, assemblyName)),
        };
    }

    private static object? TryGetAutomationProperty(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (target is null)
        {
            return null;
        }

        try
        {
            return target.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, binder: null, target, args: null);
        }
        catch (MissingMethodException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' is unavailable: {ex.Message}");
            return null;
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' is invalid: {ex.Message}");
            return null;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Automation property '{propertyName}' could not be read: {ex.Message}");
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException or ArgumentException or MissingMethodException)
        {
            Debug.WriteLine($"Automation property '{propertyName}' threw: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    private static string? TryGetAutomationString(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetAutomationProperty(target, propertyName)?.ToString();
    }

    private static bool? TryGetAutomationBoolean(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var value = TryGetAutomationProperty(target, propertyName);
        return value switch
        {
            bool boolean => boolean,
            _ when bool.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? TryGetAutomationInt32(object? target, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var value = TryGetAutomationProperty(target, propertyName);
        return value switch
        {
            byte byteValue => byteValue,
            short shortValue => shortValue,
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ when int.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static IEnumerable<object> EnumerateAutomationObjects(object? collection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (collection is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var automationObject in enumerable)
        {
            if (automationObject is not null)
            {
                yield return automationObject;
            }
        }
    }

    private static string GetReferenceName(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return string.Empty;
        }

        return identity!
            .Split([','], 2, StringSplitOptions.None)[0]
            .Trim();
    }

    private static string? NormalizeXmlValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value!.Trim();
    }

    private static string? GetElementOrAttributeValue(XElement element, string localName)
    {
        return NormalizeXmlValue(
            element.Attribute(localName)?.Value
            ?? element.Elements().FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value);
    }

    private static bool? TryParseBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static string? TryResolveProjectRelativePath(string projectDirectory, string? include)
    {
        var normalized = NormalizeXmlValue(include);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var candidate = Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(projectDirectory, normalized);

        return PathNormalization.NormalizeFilePath(candidate);
    }

    private static Project? FindProjectByPath(IEnumerable<Project> projects, string? path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var project in projects)
        {
            if (!string.IsNullOrWhiteSpace(project.FullName) &&
                PathNormalization.AreEquivalent(project.FullName, path))
            {
                return project;
            }
        }

        return null;
    }

    private static string? TryGetProjectVersion(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
        {
            return null;
        }

        return TryGetPropertyString(project.Properties, "Version")
            ?? TryGetPropertyString(project.Properties, "AssemblyVersion")
            ?? TryGetPropertyString(project.Properties, "FileVersion");
    }

    private static JObject GetDeclaredMetadata(XElement element, params string[] excludedNames)
    {
        var excluded = new HashSet<string>(excludedNames, StringComparer.OrdinalIgnoreCase);
        var metadata = new JObject();

        foreach (var attribute in element.Attributes())
        {
            if (excluded.Contains(attribute.Name.LocalName))
            {
                continue;
            }

            var value = NormalizeXmlValue(attribute.Value);
            if (value is not null)
            {
                metadata[attribute.Name.LocalName] = value;
            }
        }

        foreach (var child in element.Elements())
        {
            if (excluded.Contains(child.Name.LocalName))
            {
                continue;
            }

            var value = NormalizeXmlValue(child.Value);
            if (value is not null)
            {
                metadata[child.Name.LocalName] = value;
            }
        }

        return metadata;
    }

    private static JObject CreateDeclaredReference(
        string? name,
        string? identity,
        string? path,
        string? version,
        bool? copyLocal,
        bool? specificVersion,
        string kind,
        string origin,
        string declaredItemType,
        JObject metadata,
        Project? sourceProject = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return new JObject
        {
            ["name"] = name,
            ["identity"] = identity,
            ["path"] = path,
            ["version"] = version,
            ["culture"] = JValue.CreateNull(),
            ["publicKeyToken"] = JValue.CreateNull(),
            ["runtimeVersion"] = JValue.CreateNull(),
            ["copyLocal"] = ToJsonToken(copyLocal),
            ["specificVersion"] = ToJsonToken(specificVersion),
            ["type"] = JValue.CreateNull(),
            ["kind"] = kind,
            ["origin"] = origin,
            ["sourceProject"] = sourceProject is null ? JValue.CreateNull() : ProjectSummaryToJson(sourceProject),
            ["declared"] = true,
            ["declaredItemType"] = declaredItemType,
            ["metadata"] = metadata,
        };
    }

    private static JObject CreateDeclaredProjectReference(XElement element, string projectDirectory, IEnumerable<Project> solutionProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        var projectPath = TryResolveProjectRelativePath(projectDirectory, include);
        var sourceProject = FindProjectByPath(solutionProjects, projectPath);
        return CreateDeclaredReference(
            sourceProject?.Name ?? Path.GetFileNameWithoutExtension(include),
            include,
            projectPath,
            TryGetProjectVersion(sourceProject),
            copyLocal: null,
            specificVersion: null,
            kind: "project",
            origin: "project",
            declaredItemType: "ProjectReference",
            metadata: GetDeclaredMetadata(element, "Include"),
            sourceProject: sourceProject);
    }

    private static JObject CreateDeclaredPackageReference(XElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        return CreateDeclaredReference(
            include,
            include,
            path: null,
            version: GetElementOrAttributeValue(element, "Version"),
            copyLocal: null,
            specificVersion: null,
            kind: "package",
            origin: "package",
            declaredItemType: "PackageReference",
            metadata: GetDeclaredMetadata(element, "Include", "Version"));
    }

    private static JObject CreateDeclaredFrameworkReference(XElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        return CreateDeclaredReference(
            include,
            include,
            path: null,
            version: GetElementOrAttributeValue(element, "Version"),
            copyLocal: null,
            specificVersion: null,
            kind: FrameworkOrigin,
            origin: FrameworkOrigin,
            declaredItemType: "FrameworkReference",
            metadata: GetDeclaredMetadata(element, "Include", "Version"));
    }

    private static JObject CreateDeclaredAssemblyReference(XElement element, string projectDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var include = NormalizeXmlValue(element.Attribute("Include")?.Value) ?? string.Empty;
        var hintPath = TryResolveProjectRelativePath(projectDirectory, GetElementOrAttributeValue(element, "HintPath"));
        var origin = hintPath is not null && IsFrameworkReferencePath(hintPath)
            ? FrameworkOrigin
            : (hintPath is null ? "local" : GetReferenceOrigin(sourceProject: null, hintPath));

        return CreateDeclaredReference(
            GetReferenceName(include),
            include,
            hintPath,
            version: GetElementOrAttributeValue(element, "Version"),
            copyLocal: TryParseBoolean(GetElementOrAttributeValue(element, "Private")),
            specificVersion: TryParseBoolean(GetElementOrAttributeValue(element, "SpecificVersion")),
            kind: "assembly",
            origin: origin,
            declaredItemType: "Reference",
            metadata: GetDeclaredMetadata(element, "Include", "HintPath", "Version", "Private", "SpecificVersion"));
    }

    private static JObject? DeclaredReferenceToJson(XElement element, string projectDirectory, IEnumerable<Project> solutionProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return element.Name.LocalName switch
        {
            "ProjectReference" => CreateDeclaredProjectReference(element, projectDirectory, solutionProjects),
            "PackageReference" => CreateDeclaredPackageReference(element),
            "FrameworkReference" => CreateDeclaredFrameworkReference(element),
            "Reference" => CreateDeclaredAssemblyReference(element, projectDirectory),
            _ => null,
        };
    }

    private static JObject[] EnumerateDeclaredProjectReferences(Project project, IReadOnlyCollection<Project> solutionProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(project.FullName))
        {
            throw new CommandErrorException(UnsupportedProjectTypeCode, $"Project '{project.Name}' does not expose a project file path.");
        }

        var projectPath = PathNormalization.NormalizeFilePath(project.FullName);
        if (!File.Exists(projectPath))
        {
            throw new CommandErrorException("project_file_not_found", $"Project file not found: {projectPath}");
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new CommandErrorException(
                "project_file_read_failed",
                $"Project file could not be read: {projectPath}",
                new { exception = ex.Message });
        }

        var projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        return [.. document
            .Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "FrameworkReference" or "Reference")
            .Select(element => DeclaredReferenceToJson(element, projectDirectory, solutionProjects))
            .OfType<JObject>()
            .OrderBy(reference => reference["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetReferenceKind(Project? sourceProject, string? path)
    {
        if (sourceProject is not null)
        {
            return "project";
        }

        return string.IsNullOrWhiteSpace(path) ? "unknown" : "assembly";
    }

    private static string GetReferenceOrigin(Project? sourceProject, string? path)
    {
        if (sourceProject is not null)
        {
            return "project";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "unknown";
        }

        var normalizedPath = path!;

        if (IsFrameworkReferencePath(normalizedPath))
        {
            return FrameworkOrigin;
        }

        if (normalizedPath.IndexOf("\\.nuget\\packages\\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "package";
        }

        return "local";
    }

    private static bool IsFrameworkReferencePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path!;
        return normalizedPath.IndexOf("\\Reference Assemblies\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (normalizedPath.IndexOf("\\packs\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                normalizedPath.IndexOf("\\ref\\", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsFrameworkReference(JObject reference)
    {
        return string.Equals(reference.Value<string>("origin"), FrameworkOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static JObject ProjectReferenceToJson(object reference)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var sourceProject = TryGetAutomationProperty(reference, "SourceProject") as Project;
        var path = TryGetAutomationString(reference, "Path");
        var origin = GetReferenceOrigin(sourceProject, path);
        return new JObject
        {
            ["name"] = TryGetAutomationString(reference, "Name"),
            ["identity"] = TryGetAutomationString(reference, "Identity"),
            ["path"] = path,
            ["version"] = TryGetAutomationString(reference, "Version"),
            ["culture"] = TryGetAutomationString(reference, "Culture"),
            ["publicKeyToken"] = TryGetAutomationString(reference, "PublicKeyToken"),
            ["runtimeVersion"] = TryGetAutomationString(reference, "RuntimeVersion"),
            ["copyLocal"] = ToJsonToken(TryGetAutomationBoolean(reference, "CopyLocal")),
            ["specificVersion"] = ToJsonToken(TryGetAutomationBoolean(reference, "SpecificVersion")),
            ["type"] = ToJsonToken(TryGetAutomationInt32(reference, "Type")),
            ["kind"] = GetReferenceKind(sourceProject, path),
            ["origin"] = origin,
            ["sourceProject"] = sourceProject is null ? JValue.CreateNull() : ProjectSummaryToJson(sourceProject),
        };
    }

    // ── list-projects ─────────────────────────────────────────────────────────

    internal sealed class IdeListProjectsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x023E)
    {
        protected override string CanonicalName => "Tools.IdeListProjects";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var startupProjects = GetStartupProjects(context.Dte);
            var projects = EnumerateAllProjects(context.Dte)
                .Select(p => ProjectToJson(p, startupProjects))
                .ToArray();

            return new CommandExecutionResult(
                $"Found {projects.Length} project(s).",
                new JObject { ["count"] = projects.Length, ["projects"] = new JArray(projects) });
        }
    }

    // ── add-project ───────────────────────────────────────────────────────────

    internal sealed class IdeAddProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x023F)
    {
        protected override string CanonicalName => "Tools.IdeAddProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var projectPath = PathNormalization.NormalizeFilePath(args.GetRequiredString("project"));
            if (!File.Exists(projectPath))
                throw new CommandErrorException("file_not_found", $"Project file not found: {projectPath}");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            Project added;
            var folderName = args.GetString("solution-folder");
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                var folder = FindOrCreateSolutionFolder(context.Dte, folderName!);
                added = ((SolutionFolder)folder.Object).AddFromFile(projectPath);
            }
            else
            {
                added = context.Dte.Solution.AddFromFile(projectPath);
            }

            return new CommandExecutionResult(
                $"Project '{added.Name}' added to solution.",
                new JObject { ["name"] = added.Name, [UniqueNamePropertyName] = added.UniqueName, ["path"] = added.FullName });
        }

        private static Project FindOrCreateSolutionFolder(DTE2 dte, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Project p in dte.Solution.Projects)
            {
                if (string.Equals(p.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
            return ((Solution2)dte.Solution).AddSolutionFolder(name);
        }
    }

    // ── remove-project ────────────────────────────────────────────────────────

    // ── create-project ────────────────────────────────────────────────────────

    internal sealed class IdeCreateProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0260)
    {
        protected override string CanonicalName => "Tools.IdeCreateProject";

        private static readonly Dictionary<string, string> TemplateAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["classlib"] = "ClassLibrary",
            ["class-library"] = "ClassLibrary",
            ["console"] = "ConsoleApplication",
            ["wpf"] = "WpfApplication",
            ["winforms"] = "WindowsFormsApplication",
            ["web"] = "WebApplication",
            ["webapi"] = "WebApiApplication",
            ["razor"] = "RazorClassLibrary",
            ["test"] = "TestProject",
            ["xunit"] = "xUnitTestProject",
            ["nunit"] = "NUnitTestProject",
            ["mstest"] = "MSTestProject",
        };

        private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "CSharp",
            ["csharp"] = "CSharp",
            ["c#"] = "CSharp",
            ["vb"] = "VisualBasic",
            ["fs"] = "FSharp",
            ["fsharp"] = "FSharp",
            ["f#"] = "FSharp",
            ["cpp"] = "VC",
            ["c++"] = "VC",
        };

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var name = args.GetRequiredString("name");
            var templateName = args.GetString("template") ?? "ClassLibrary";
            var language = args.GetString("language") ?? "CSharp";
            var directory = args.GetString("directory");
            var folderName = args.GetString("solution-folder");

            // Resolve aliases
            if (TemplateAliases.TryGetValue(templateName, out var resolvedTemplate))
            {
                templateName = resolvedTemplate;
            }
            if (LanguageAliases.TryGetValue(language, out var resolvedLanguage))
            {
                language = resolvedLanguage;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var solution = (Solution2)context.Dte.Solution;

            // Resolve directory: default to solution dir / src / name
            if (string.IsNullOrWhiteSpace(directory))
            {
                var solutionDir = Path.GetDirectoryName(solution.FullName) ?? ".";
                var srcDir = Path.Combine(solutionDir, "src");
                directory = Path.Combine(Directory.Exists(srcDir) ? srcDir : solutionDir, name);
            }
            directory = PathNormalization.NormalizeFilePath(directory!);

            if (Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0)
            {
                throw new CommandErrorException("already_exists",
                    $"Directory already exists and is not empty: {directory}. Choose a different name or directory.");
            }

            // Get template path from VS
            string templatePath;
            try
            {
                templatePath = solution.GetProjectTemplate(templateName, language);
            }
            catch (Exception ex)
            {
                throw new CommandErrorException("template_not_found",
                    $"Could not find project template '{templateName}' for language '{language}'. " +
                    $"Common aliases: classlib, console, wpf, winforms, web, webapi, test, xunit, nunit, mstest. " +
                    $"You can also pass any VS template name directly (e.g. BlazorServerApp, WorkerService). Error: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(templatePath))
            {
                throw new CommandErrorException("template_not_found",
                    $"Template '{templateName}' not found for language '{language}'. " +
                    "Common aliases: classlib, console, wpf, winforms, web, webapi, test, xunit, nunit, mstest. " +
                    "You can also pass any VS template name directly (e.g. BlazorServerApp, WorkerService).");
            }

            // Create the project
            Directory.CreateDirectory(directory);

            if (!string.IsNullOrWhiteSpace(folderName))
            {
                var folder = FindOrCreateSolutionFolder(context.Dte, folderName!);
                ((SolutionFolder)folder.Object).AddFromTemplate(templatePath, directory, name);
            }
            else
            {
                solution.AddFromTemplate(templatePath, directory, name, false);
            }

            // Find the created project
            Project? created = null;
            foreach (Project p in solution.Projects)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    created = p;
                    break;
                }
            }

            var projectPath = created?.FullName ?? Path.Combine(directory, name + ".csproj");

            return new CommandExecutionResult(
                $"Created '{templateName}' project '{name}' at {directory}.",
                new JObject
                {
                    ["name"] = created?.Name ?? name,
                    [UniqueNamePropertyName] = created?.UniqueName ?? name,
                    ["path"] = projectPath,
                    ["template"] = templateName,
                    ["language"] = language,
                    ["directory"] = directory,
                });
        }

        private static Project FindOrCreateSolutionFolder(DTE2 dte, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Project p in dte.Solution.Projects)
            {
                if (string.Equals(p.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
            return ((Solution2)dte.Solution).AddSolutionFolder(name);
        }
    }

    internal sealed class IdeRemoveProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0240)
    {
        protected override string CanonicalName => "Tools.IdeRemoveProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, query)
                ?? throw new CommandErrorException(ProjectNotFoundCode, $"Project not found: {query}");

            var name = project.Name;
            context.Dte.Solution.Remove(project);

            return new CommandExecutionResult(
                $"Project '{name}' removed from solution.",
                new JObject { ["name"] = name });
        }
    }

    // ── set-startup-project ───────────────────────────────────────────────────

    internal sealed class IdeSetStartupProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0241)
    {
        protected override string CanonicalName => "Tools.IdeSetStartupProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var query = args.GetRequiredString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, query)
                ?? throw new CommandErrorException(ProjectNotFoundCode, $"Project not found: {query}");

            context.Dte.Solution.SolutionBuild.StartupProjects = project.UniqueName;

            return new CommandExecutionResult(
                $"Startup project set to '{project.Name}'.",
                new JObject { ["name"] = project.Name, [UniqueNamePropertyName] = project.UniqueName });
        }
    }

    // ── add-file-to-project ───────────────────────────────────────────────────

    internal sealed class IdeAddFileToProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0242)
    {
        protected override string CanonicalName => "Tools.IdeAddFileToProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var filePath = PathNormalization.NormalizeFilePath(args.GetRequiredString("file"));
            var projectQuery = args.GetRequiredString("project");
            if (!File.Exists(filePath))
                throw new CommandErrorException("file_not_found", $"File not found: {filePath}");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            var projectItem = project.ProjectItems.AddFromFile(filePath);

            return new CommandExecutionResult(
                $"File '{Path.GetFileName(filePath)}' added to project '{project.Name}'.",
                new JObject { ["file"] = filePath, ["fileName"] = projectItem.Name, ["project"] = project.Name });
        }
    }

    // ── remove-file-from-project ──────────────────────────────────────────────

    internal sealed class IdeRemoveFileFromProjectCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0243)
    {
        protected override string CanonicalName => "Tools.IdeRemoveFileFromProject";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var fileQuery = args.GetRequiredString("file");
            var projectQuery = args.GetRequiredString("project");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            var projectItem = FindProjectItem(project.ProjectItems, fileQuery)
                ?? throw new CommandErrorException("file_not_found", $"File '{fileQuery}' not found in project '{project.Name}'.");

            var fileName = projectItem.Name;
            projectItem.Remove(); // removes from project, does not delete from disk

            return new CommandExecutionResult(
                $"File '{fileName}' removed from project '{project.Name}'.",
                new JObject { ["fileName"] = fileName, ["project"] = project.Name });
        }

        private static ProjectItem? FindProjectItem(ProjectItems items, string query)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var queryFileName = Path.GetFileName(query);
            foreach (ProjectItem item in items)
            {
                if (string.Equals(item.Name, queryFileName, StringComparison.OrdinalIgnoreCase) ||
                    (item.FileCount > 0 && PathNormalization.AreEquivalent(item.FileNames[1], query)))
                {
                    return item;
                }
                if (item.ProjectItems is { Count: > 0 })
                {
                    var found = FindProjectItem(item.ProjectItems, query);
                    if (found is not null) return found;
                }
            }
            return null;
        }
    }

    // ── query-project-items ────────────────────────────────────────────────────

    internal sealed class IdeQueryProjectItemsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0245)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectItems";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var projectQuery = args.GetRequiredString("project");
            var pathFilter = args.GetString("path");
            var max = args.GetInt32("max", 500);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            var solutionDirectory = Path.GetDirectoryName(context.Dte.Solution.FullName) ?? string.Empty;
            var items = new List<JObject>();
            foreach (var projectItem in EnumerateProjectItems(project.ProjectItems))
            {
                var paths = GetProjectItemPaths(projectItem);
                if (!MatchesPathFilter(paths, pathFilter, solutionDirectory))
                {
                    continue;
                }

                items.Add(ProjectItemToJson(projectItem, paths));
                if (items.Count >= max)
                {
                    break;
                }
            }

            return new CommandExecutionResult(
                $"Found {items.Count} project item(s) in '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["pathFilter"] = pathFilter,
                    ["count"] = items.Count,
                    ["items"] = new JArray(items),
                });
        }
    }

    // ── query-project-properties ───────────────────────────────────────────────

    internal sealed class IdeQueryProjectPropertiesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0246)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectProperties";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var projectQuery = args.GetRequiredString("project");
            var requestedNames = SplitRequestedNames(args.GetString("names"));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            var properties = new JObject();
            var missing = new JArray();
            if (requestedNames.Length == 0)
            {
                foreach (var (name, value) in EnumerateProjectProperties(project))
                {
                    properties[name] = value;
                }
            }
            else
            {
                foreach (var name in requestedNames)
                {
                    var value = TryGetNormalizedPropertyValue(project, name);
                    if (value is null)
                    {
                        missing.Add(name);
                        continue;
                    }

                    properties[name] = value;
                }
            }

            return new CommandExecutionResult(
                $"Read {properties.Count} project propert{(properties.Count == 1 ? "y" : "ies")} from '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["count"] = properties.Count,
                    ["properties"] = properties,
                    ["missing"] = missing,
                });
        }
    }

    // ── query-project-configurations ───────────────────────────────────────────

    internal sealed class IdeQueryProjectConfigurationsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0247)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectConfigurations";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var projectQuery = args.GetRequiredString("project");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            ConfigurationManager? configurationManager;
            try
            {
                configurationManager = project.ConfigurationManager;
            }
            catch (COMException ex)
            {
                throw new CommandErrorException(
                    UnsupportedProjectTypeCode,
                    $"Project '{project.Name}' does not expose configurations.",
                    new { exception = ex.Message });
            }

            if (configurationManager is null)
            {
                throw new CommandErrorException(
                    UnsupportedProjectTypeCode,
                    $"Project '{project.Name}' does not expose configurations.");
            }

            Configuration? activeConfiguration = null;
            try
            {
                activeConfiguration = configurationManager.ActiveConfiguration;
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"Unable to read active configuration for '{project.Name}': {ex.Message}");
            }

            var configurations = EnumerateProjectConfigurations(configurationManager)
                .Select(configuration => ProjectConfigurationToJson(configuration, activeConfiguration))
                .OrderBy(configuration => configuration["name"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new CommandExecutionResult(
                $"Found {configurations.Length} configuration(s) for '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["active"] = GetConfigurationMoniker(activeConfiguration),
                    ["count"] = configurations.Length,
                    ["configurations"] = new JArray(configurations),
                });
        }
    }

    // ── query-project-references ───────────────────────────────────────────────

    internal sealed class IdeQueryProjectReferencesCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0248)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectReferences";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var projectQuery = args.GetRequiredString("project");
            var includeFramework = args.GetBoolean("include-framework", false);
            var declaredOnly = args.GetBoolean("declared-only", false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            JObject[] allReferences;
            if (declaredOnly)
            {
                var solutionProjects = EnumerateAllProjects(context.Dte).ToArray();
                allReferences = EnumerateDeclaredProjectReferences(project, solutionProjects);
            }
            else
            {
                var referencesObject = TryGetAutomationProperty(project.Object, "References")
                    ?? throw new CommandErrorException(
                        UnsupportedProjectTypeCode,
                        $"Project '{project.Name}' does not expose automation references.");

                allReferences =
                [
                    .. EnumerateAutomationObjects(referencesObject)
                        .Select(ProjectReferenceToJson)
                        .OrderBy(reference => reference["name"]?.ToString(), StringComparer.OrdinalIgnoreCase),
                ];
            }

            var references = includeFramework
                ? allReferences
                : [.. allReferences.Where(reference => !IsFrameworkReference(reference))];

            var omittedFrameworkCount = allReferences.Length - references.Length;

            return new CommandExecutionResult(
                $"Found {references.Length} {(declaredOnly ? "declared " : string.Empty)}reference(s) for '{project.Name}'.",
                new JObject
                {
                    ["project"] = project.Name,
                    [UniqueNamePropertyName] = project.UniqueName,
                    ["count"] = references.Length,
                    ["totalCount"] = allReferences.Length,
                    ["declaredOnly"] = declaredOnly,
                    ["includeFramework"] = includeFramework,
                    ["omittedFrameworkCount"] = omittedFrameworkCount,
                    ["references"] = new JArray(references),
                });
        }
    }

    // ── query-project-outputs ─────────────────────────────────────────────────

    internal sealed class IdeQueryProjectOutputsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0249)
    {
        protected override string CanonicalName => "Tools.IdeQueryProjectOutputs";

        protected override async Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var projectQuery = args.GetRequiredString("project");
            var requestedConfiguration = NormalizeXmlValue(args.GetString("configuration"));
            var requestedPlatform = NormalizeXmlValue(args.GetString("platform"));
            var requestedTargetFramework = NormalizeXmlValue(args.GetString("target-framework"));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnsureSolutionOpen(context.Dte);

            var project = FindProject(context.Dte, projectQuery)
                ?? throw CreateProjectNotFound(projectQuery);

            var activeConfiguration = TryGetActiveProjectConfiguration(project);
            var configurationName = requestedConfiguration
                ?? NormalizeXmlValue(activeConfiguration?.ConfigurationName)
                ?? NormalizeXmlValue(context.Dte.Solution?.SolutionBuild?.ActiveConfiguration?.Name?.Split('|').FirstOrDefault())
                ?? "Debug";
            var platformName = requestedPlatform
                ?? NormalizeXmlValue(activeConfiguration?.PlatformName)
                ?? NormalizeXmlValue(context.Dte.Solution?.SolutionBuild?.ActiveConfiguration?.Name?.Split('|').Skip(1).FirstOrDefault())
                ?? "Any CPU";
            var targetFramework = requestedTargetFramework
                ?? GetPrimaryValue(GetNormalizedProjectPropertyString(project, "TargetFramework"));
            var assemblyName = GetNormalizedProjectPropertyString(project, "AssemblyName")
                ?? project.Name;
            var outputType = GetNormalizedOutputType(project);
            var output = ProjectOutputToJson(project, configurationName, platformName, targetFramework, assemblyName, outputType);

            return new CommandExecutionResult(
                $"Resolved project outputs for '{project.Name}'.",
                output);
        }
    }

    // ── search-solutions ──────────────────────────────────────────────────────

    internal sealed class IdeSearchSolutionsCommand(VsIdeBridgePackage package, IdeBridgeRuntime runtime, OleMenuCommandService commandService)
        : IdeCommandBase(package, runtime, commandService, 0x0244)
    {
        protected override string CanonicalName => "Tools.IdeSearchSolutions";

        protected override Task<CommandExecutionResult> ExecuteAsync(IdeCommandContext context, CommandArguments args)
        {
            var rootPath = args.GetString("path") ?? GetDefaultSearchRoot();
            var query = args.GetString("query");
            var maxDepth = args.GetInt32("max-depth", 6);
            var maxResults = args.GetInt32("max", 200);

            if (!Directory.Exists(rootPath))
                throw new CommandErrorException("path_not_found", $"Search root not found: {rootPath}");

            var matches = FindSolutions(rootPath, query, maxDepth, maxResults);
            var results = matches
                .Select(f => new JObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["fileName"] = Path.GetFileName(f),
                    ["path"] = f,
                    ["directory"] = Path.GetDirectoryName(f),
                    ["lastModified"] = File.GetLastWriteTime(f).ToString("O"),
                })
                .ToArray();

            return Task.FromResult(new CommandExecutionResult(
                $"Found {results.Length} solution(s) under '{rootPath}'.",
                new JObject { ["count"] = results.Length, ["root"] = rootPath, ["solutions"] = new JArray(results) }));
        }

        private static string GetDefaultSearchRoot()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var reposPath = Path.Combine(userProfile, "source", "repos");
            return Directory.Exists(reposPath) ? reposPath : userProfile;
        }

        private static List<string> FindSolutions(string root, string? query, int maxDepth, int maxResults)
        {
            var results = new List<string>();
            SearchDirectory(root, query, maxDepth, 0, results, maxResults);
            results.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            return results;
        }

        private static void SearchDirectory(string dir, string? query, int maxDepth, int depth, List<string> results, int maxResults)
        {
            if (depth > maxDepth || results.Count >= maxResults) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")))
                {
                    if (results.Count >= maxResults) break;
                    if (query is null || Path.GetFileNameWithoutExtension(file).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(file);
                }
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    if (results.Count >= maxResults) break;
                    var name = Path.GetFileName(subDir);
                    if (name.StartsWith(".") || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                        continue;
                    SearchDirectory(subDir, query, maxDepth, depth + 1, results, maxResults);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Skipping solution search directory '{dir}': {ex.Message}");
            }
            catch (DirectoryNotFoundException ex)
            {
                Debug.WriteLine($"Skipping missing solution search directory '{dir}': {ex.Message}");
            }
        }
    }
}
