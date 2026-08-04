using System.Xml.Linq;

namespace Architecture.Tests;

internal sealed record ProjectReferenceEdge(string Source, string Target);

internal sealed class RepositoryModel
{
    private RepositoryModel(string root, IReadOnlyList<string> projects, IReadOnlyList<ProjectReferenceEdge> references)
    {
        Root = root;
        Projects = projects;
        References = references;
    }

    public string Root { get; }

    public IReadOnlyList<string> Projects { get; }

    public IReadOnlyList<ProjectReferenceEdge> References { get; }

    public static RepositoryModel Load()
    {
        var root = FindRoot(AppContext.BaseDirectory);
        var projects = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var edges = new List<ProjectReferenceEdge>();

        foreach (var project in projects)
        {
            var document = XDocument.Load(project, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants("ProjectReference"))
            {
                var include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    throw new InvalidDataException($"ProjectReference without Include in '{project}'.");
                }

                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                edges.Add(new ProjectReferenceEdge(project, target));
            }
        }

        return new RepositoryModel(root, projects, edges);
    }

    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static string FindRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing AggregatorBackend.slnx was not found.");
    }
}
