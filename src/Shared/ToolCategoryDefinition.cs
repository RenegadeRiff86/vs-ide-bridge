namespace VsIdeBridge.Shared;

public sealed class ToolCategoryDefinition
{
    public ToolCategoryDefinition(string name, string summary, string description)
    {
        Name = name;
        Summary = summary;
        Description = description;
    }

    public string Name { get; }

    public string Summary { get; }

    public string Description { get; }
}
