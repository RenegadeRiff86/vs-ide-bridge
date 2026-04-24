using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string ProjectDescriptionProperty = "description";
    private const string ListProjectsTool = "list_projects";
    private const string QueryProjectItemsTool = "query_project_items";
    private const string QueryProjectPropertiesTool = "query_project_properties";

    private static IEnumerable<ToolEntry> ProjectTools()
        =>
        ProjectQueryTools()
            .Concat(ProjectManagementTools())
            .Concat(ProjectPythonTools());

    private static IEnumerable<ToolEntry> ProjectQueryTools()
    {
        yield return BridgeTool(ListProjectsTool,
            "List all projects in the open solution with their names, paths, and metadata. Use project names from the result to target specific projects in other commands like build, query_project_properties, or query_project_items.",
            EmptySchema(), "list-projects", _ => Empty(), Project,
            outputSchema: BuildListProjectsOutputSchema(),
            searchHints: BuildSearchHints(
                workflow: [("build", "Build a specific project by name"), (QueryProjectPropertiesTool, "Read properties for a listed project")],
                related: [(QueryProjectItemsTool, "List files in a project"), ("query_project_references", "List references for a project")]));

        yield return BridgeTool(QueryProjectItemsTool,
            "List items in a project with FileArg paths, kinds, and item types.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Opt(Path, "Optional path filter."),
                OptInt(Max, "Max items to return (default 200).")),
            "query-project-items",
            a => Build(
                (Project, OptionalString(a, Project)),
                (Path, OptionalString(a, Path)),
                (Max, OptionalText(a, Max))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [("read_file", "Read a listed file"), ("add_file_to_project", "Add a missing file to the project")],
                related: [(QueryProjectPropertiesTool, "Read project properties"), ("file_outline", "Get symbol structure of a listed file")]));

        yield return BridgeTool(QueryProjectPropertiesTool,
            "Read MSBuild project properties from one project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                OptArr("names", "Property names to read.")),
            "query-project-properties",
            a => Build(
                (Project, OptionalString(a, Project)),
                ("names", OptionalStringArray(a, "names"))),
            Project,
            searchHints: BuildSearchHints(
                related: [(QueryProjectItemsTool, "List files in the project"), ("query_project_references", "List project references"), ("build", "Build after reviewing properties")]));

        yield return BridgeTool("query_project_configurations",
            "List project configurations and platforms for one project.",
            ObjectSchema(Req(Project, ProjectDesc)),
            "query-project-configurations",
            a => Build((Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                related: [("set_build_configuration", "Switch to a different configuration"), ("build", "Build with the active configuration")]));

        yield return BridgeTool("query_project_references",
            "List project references for one project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                OptBool("declared_only", "Return only declared (project-FileArg) references."),
                OptBool("include_framework",
                    "Include framework assembly references (default false).")),
            "query-project-references",
            a => Build(
                (Project, OptionalString(a, Project)),
                BoolArg("declared-only", a, "declared_only", false, true),
                BoolArg("include-framework", a, "include_framework", false, true)),
            Project,
            searchHints: BuildSearchHints(
                workflow: [("scan_project_dependencies", "Get a full dependency health scan")],
                related: [("nuget_add_package", "Add a NuGet package"), (QueryProjectItemsTool, "List source files")]));

        yield return BridgeTool("query_project_outputs",
            "Resolve the primary output artifact and output directory for one project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Opt(Configuration, "Build configuration."),
                Opt("target_framework", "Target framework moniker.")),
            "query-project-outputs",
            a => Build(
                (Project, OptionalString(a, Project)),
                (Configuration, OptionalString(a, Configuration)),
                ("target-framework", OptionalString(a, "target_framework"))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [("build", "Build the project to produce the output")],
                related: [(QueryProjectPropertiesTool, "Read OutputPath and TargetFramework directly"), ("query_project_configurations", "List available configurations")]));
    }

    private static JsonObject BuildListProjectsOutputSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["count"] = new JsonObject { ["type"] = "integer", [ProjectDescriptionProperty] = "Number of projects found." },
                ["projects"] = new JsonObject
                {
                    ["type"] = "array",
                    [ProjectDescriptionProperty] = "Array of project objects with name, path, and metadata.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string", [ProjectDescriptionProperty] = "The display name of the project." },
                            ["uniqueName"] = new JsonObject { ["type"] = "string", [ProjectDescriptionProperty] = "The unique identifier for the project (includes solution folders)." },
                            ["path"] = new JsonObject { ["type"] = "string", [ProjectDescriptionProperty] = "The absolute file system path to the project file." },
                            ["kind"] = new JsonObject { ["type"] = "string", [ProjectDescriptionProperty] = "The project type identifier (e.g., project kind GUID)." },
                            ["isStartup"] = new JsonObject { ["type"] = "boolean", [ProjectDescriptionProperty] = "Whether this is the solution startup project." },
                        },
                        ["required"] = new JsonArray { "name", "uniqueName", "path", "kind", "isStartup" },
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JsonArray { "count", "projects" },
            ["additionalProperties"] = false,
        };

    private static IEnumerable<ToolEntry> ProjectManagementTools()
    {
        yield return BridgeTool("add_project",
            "Add an existing or new project to the solution.",
            ObjectSchema(
                Req(Project, "Absolute path to the project FileArg."),
                Opt("solution_folder", "Optional solution folder name.")),
            "add-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                ("solution-folder", OptionalString(a, "solution_folder"))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [(ListProjectsTool, "Confirm the project was added"), ("build", "Build after adding")],
                related: [("create_project", "Create a new project instead"), ("remove_project", "Remove a project")]));

        yield return BridgeTool("remove_project",
            "Remove a project from the solution by name or path.",
            ObjectSchema(Req(Project, "Project name or path to remove.")),
            "remove-project",
            a => Build((Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                related: [(ListProjectsTool, "List remaining projects"), ("add_project", "Re-add the project")]));

        yield return BridgeTool("rename_project",
            "Rename a project within the solution. This updates the project name shown by Visual Studio, but does not rename folders or the project file on disk.",
            ObjectSchema(
                Req(Project, "Project name or path to rename."),
                Req("new_name", "New project name to show in the solution.")),
            "rename-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                ("new-name", OptionalString(a, "new_name"))),
            Project,
            searchHints: BuildSearchHints(
                related: [(ListProjectsTool, "Confirm the rename"), (QueryProjectPropertiesTool, "Read project properties")]));

        yield return BridgeTool("create_project",
            "Create a new project and add it to the open solution.",
            ObjectSchema(
                Req("name", "New project name."),
                Opt("template", "Project template name or identifier."),
                Opt("language", "Programming language (e.g. C#, VB, F#)."),
                Opt("directory", "Directory to create the project in."),
                Opt("solution_folder", "Optional solution folder name.")),
            "create-project",
            a => Build(
                ("name", OptionalString(a, "name")),
                ("template", OptionalString(a, "template")),
                ("language", OptionalString(a, "language")),
                ("directory", OptionalString(a, "directory")),
                ("solution-folder", OptionalString(a, "solution_folder"))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [(ListProjectsTool, "Confirm the project was created"), ("build", "Build the new project")],
                related: [("add_project", "Add an existing project instead"), ("add_file_to_project", "Add source files to the new project")]));

        yield return BridgeTool("set_startup_project",
            "Set the solution startup project by name or path.",
            ObjectSchema(Req(Project, ProjectDesc)),
            "set-startup-project",
            a => Build((Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [("debug_start", "Start debugging the startup project"), ("build", "Build the startup project")],
                related: [(ListProjectsTool, "List projects to find the right name")]));

        yield return BridgeTool("add_file_to_project",
            "Add an existing FileArg to a project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Req(FileArg, "Absolute path to the FileArg.")),
            "add-file-to-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                (FileArg, OptionalString(a, FileArg))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [("read_file", "Read the added file"), ("build", "Build after adding")],
                related: [("remove_file_from_project", "Remove a file from the project"), ("query_project_items", "List current project files")]));

        yield return BridgeTool("remove_file_from_project",
            "Remove a FileArg from a project.",
            ObjectSchema(
                Req(Project, ProjectDesc),
                Req(FileArg, "FileArg path to remove.")),
            "remove-file-from-project",
            a => Build(
                (Project, OptionalString(a, Project)),
                (FileArg, OptionalString(a, FileArg))),
            Project,
            searchHints: BuildSearchHints(
                related: [("add_file_to_project", "Re-add the file"), ("query_project_items", "List remaining project files")]));
    }

    private static IEnumerable<ToolEntry> ProjectPythonTools()
    {
        yield return BridgeTool("python_set_project_env",
            "Set the active Python interpreter for the active Python project in Visual Studio.",
            ObjectSchema(
                Req(Path, "Absolute path to the Python interpreter."),
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "set-python-project-env",
            a => Build(
                (Path, OptionalString(a, Path)),
                (Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                workflow: [("python_list_envs", "Discover available interpreters first")],
                related: [("python_sync_env", "Sync bridge interpreter to VS project")]));

        yield return BridgeTool("python_set_startup_file",
            "Set the startup FileArg for the active Python project.",
            ObjectSchema(
                Req(FileArg, "Path to the Python FileArg to set as startup."),
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "set-python-startup-FileArg",
            a => Build(
                (FileArg, OptionalString(a, FileArg)),
                (Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                related: [("python_get_startup_file", "Read the current startup file")]));

        yield return BridgeTool("python_get_startup_file",
            "Get the startup FileArg configured for the active Python project.",
            ObjectSchema(
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "get-python-startup-FileArg",
            a => Build((Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                related: [("python_set_startup_file", "Change the startup file")]));

        yield return BridgeWrapperTool("python_sync_env",
            "Sync the active bridge Python interpreter to the active Python project in Visual Studio.",
            ObjectSchema(
                Opt(Project, "Python project name or path. Defaults to the active project.")),
            "set-python-project-env",
            a => Build(
                (Path, PythonInterpreterState.LoadActiveInterpreterPath()),
                (Project, OptionalString(a, Project))),
            Project,
            searchHints: BuildSearchHints(
                related: [("python_set_project_env", "Set a specific interpreter"), ("python_list_envs", "List available interpreters")]));
    }
}
