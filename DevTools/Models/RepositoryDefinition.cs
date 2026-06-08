namespace DevTools.Models;

public sealed class RepositoryDefinition
{
    public RepositoryDefinition(string owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    public string Owner { get; }

    public string Name { get; }

    public string FullName => $"{Owner}/{Name}";

    public override string ToString() => FullName;
}
