using System.Text.Json;
using System.Text.Json.Serialization;
using SceneBuilder.DocGen;

var outPath = ArgValue("--out");
if (outPath is null)
{
    Console.Error.WriteLine("usage: dotnet run --project SceneBuilder.DocGen -- --out <path/to/api.json>");
    Console.Error.WriteLine("       [--runtime <dir>] [--descriptors <file>]");
    return 2;
}

var repoRoot = FindRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("SceneBuilder.sln not found above the working directory.");
    return 2;
}

var runtimeDir = ArgValue("--runtime") ?? Path.Combine(repoRoot, "com.codescenes", "Runtime");
var descriptorsFile = ArgValue("--descriptors")
    ?? Path.Combine(repoRoot, "CodeScenes.Analyzers", "DiagnosticDescriptors.cs");

if (!Directory.Exists(runtimeDir))
{
    Console.Error.WriteLine($"authoring source not found: {runtimeDir}");
    return 2;
}

var document = new ApiDocument
{
    Assembly = "SceneBuilder.Authoring",
    Namespace = "SceneBuilder.Authoring",
    Types = TypeExtractor.FromDirectory(runtimeDir, repoRoot),
    Diagnostics = DiagnosticExtractor.FromFile(descriptorsFile),
};

new CrefResolver(AllTypes(document.Types)).ResolveDocument(document);

var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var json = JsonSerializer.Serialize(document, options);
var full = Path.GetFullPath(outPath);
Directory.CreateDirectory(Path.GetDirectoryName(full)!);
File.WriteAllText(full, json + "\n");

var memberCount = AllTypes(document.Types).Sum(t => t.Members.Count);
Console.WriteLine(
    $"{document.Types.Count} types, {memberCount} members, {document.Diagnostics.Count} diagnostics -> {full}");
return 0;

static IEnumerable<ApiType> AllTypes(IEnumerable<ApiType> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
    }
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string? FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SceneBuilder.sln"))) return directory.FullName;
        directory = directory.Parent;
    }

    directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SceneBuilder.sln"))) return directory.FullName;
        directory = directory.Parent;
    }

    return null;
}
