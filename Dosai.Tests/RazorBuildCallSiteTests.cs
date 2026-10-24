using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

// A call in a .razor or .cshtml file exists only in the build: the Razor compiler turns the
// template into C# whose #line directives point back at the template, and no source file holds it.
public sealed class RazorBuildCallSiteTests
{
    private const string ProgramSource = """
        using System;

        namespace App;

        public static class Program
        {
            public static void Main(string[] args)
            {
                Console.WriteLine(Vendor.Json.Serialize(args.Length));
            }
        }
        """;

    private const int ProgramSerializeLine = 9;

    private const string TemplateGeneratedSource = """
        #pragma warning disable 1591
        namespace App.Components
        {
            #line hidden
            public partial class Template
            {
                private int currentCount;

                public string Render()
                {
                    System.GC.KeepAlive(this);
        #nullable restore
        #line 10 "{document}"
                    var json = Vendor.Json.Serialize(currentCount);
        #line default
        #line hidden
        #nullable disable
                    return json;
                }
            }
        }
        """;

    private const int TemplateSerializeLine = 10;

    private const string GeneratedDocument = "Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Template.g.cs";

    private const string VendorSource = """
        namespace Vendor;

        public static class Json
        {
            public static string Serialize(object value) => value.ToString() ?? string.Empty;
        }
        """;

    private const string VendorDocument = "/_/src/Vendor/Shared/Widget.razor";

    public static TheoryData<string, string> TemplateBuilds() => new()
    {
        { "{root}/", Path.Combine("Components", "Pages", "Counter.razor") },
        { "/_/", Path.Combine("Components", "Pages", "Counter.razor") },
        { "{root}/", Path.Combine("Pages", "Privacy.cshtml") },
        { "{root}/", Path.Combine("Components", "Layout", "NavMenu.Razor") }
    };

    [Theory]
    [MemberData(nameof(TemplateBuilds))]
    public void Methods_ACallInATemplateOfTheTreesBuild_IsListedAtItsTemplateLine(string documentRoot, string template)
    {
        using var fixture = BuildFixture(documentRoot, template);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var call = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod == "Serialize" && call.FileName == Path.GetFileName(template));
        Assert.Equal((AnalysisEvidenceKind.AssemblyIlDirect, Path.Combine("src", "App", template), TemplateSerializeLine), (call.EvidenceKind, call.Path, call.LineNumber));
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CalledMethodName == "Serialize" && edge.CallLocation.FileName == Path.GetFileName(template) && edge.CallLocation.LineNumber == TemplateSerializeLine);
    }

    [Fact]
    public void Methods_TheTreesBuildUnderBin_AddsNothingOutsideItsTemplates()
    {
        using var fixture = BuildFixture("{root}/", Path.Combine("Components", "Pages", "Counter.razor"));
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        var calls = slice.MethodCalls ?? [];

        Assert.All(calls.Where(IsIlCall), call => Assert.Equal("Counter.razor", call.FileName));
        Assert.Single(calls, call => call.CalledMethod is { } called && called.Contains("Serialize", StringComparison.Ordinal) && call.Path == Path.Combine("src", "App", "Program.cs") && call.LineNumber == ProgramSerializeLine);
        Assert.DoesNotContain(calls, call => call.CallerNamespace == "Vendor");
        Assert.DoesNotContain(slice.CallGraph!.Nodes, node => node.Name == "KeepAlive");
        var nodeIds = slice.CallGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(slice.CallGraph.Edges, edge => Assert.True(nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId), $"{edge.SourceId} -> {edge.TargetId}"));
    }

    [Fact]
    public void Methods_ACopyOfTheBuildOutsideBin_KeepsEveryCallOfTheBuild()
    {
        using var fixture = BuildFixture("{root}/", Path.Combine("Components", "Pages", "Counter.razor"));
        var output = Path.Combine(fixture.Path, "out");
        Directory.CreateDirectory(output);
        foreach (var file in Directory.EnumerateFiles(BinDirectory(fixture.Path)))
        {
            File.Copy(file, Path.Combine(output, Path.GetFileName(file)));
        }

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        var ilCalls = (slice.MethodCalls ?? []).Where(IsIlCall).ToList();

        Assert.Contains(ilCalls, call => call.CalledMethod == "Serialize" && call.FileName == "Program.cs" && call.LineNumber == ProgramSerializeLine);
        Assert.Single(ilCalls, call => call.CalledMethod == "Serialize" && call.FileName == "Counter.razor");
    }

    private static bool IsIlCall(MethodCalls call) => call.EvidenceKind is AnalysisEvidenceKind.AssemblyIlDirect or AnalysisEvidenceKind.AssemblyIlDelegateTarget or AnalysisEvidenceKind.AssemblyIlVirtualCandidate or AnalysisEvidenceKind.AssemblyIlGeneratedState;

    private static string BinDirectory(string root) => Path.Combine(root, "src", "App", "bin", "Debug", "net10.0");

    // documentRoot "{root}/" names the PDB's documents by their path in the tree, "/_/" as a deterministic build does.
    private static TempDir BuildFixture(string documentRoot, string template)
    {
        var fixture = new TempDir();
        var project = Path.Combine(fixture.Path, "src", "App");
        var templateFile = Path.Combine(project, template);
        var bin = BinDirectory(fixture.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(templateFile)!);
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), ProgramSource);
        File.WriteAllText(templateFile, "<p>@Vendor.Json.Serialize(currentCount)</p>");

        var documents = documentRoot.Replace("{root}", fixture.Path.Replace('\\', '/'), StringComparison.Ordinal) + "src/App/";
        var vendor = Emit(bin, "Vendor", [(VendorSource, VendorDocument)]);
        Emit(bin, "App",
            [
                (ProgramSource, documents + "Program.cs"),
                (TemplateGeneratedSource.Replace("{document}", documents + template.Replace('\\', '/'), StringComparison.Ordinal), GeneratedDocument)
            ],
            vendor);
        return fixture;
    }

    private static string Emit(string outputDirectory, string assemblyName, IEnumerable<(string Source, string Document)> sources, string? reference = null)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        if (reference is not null) metadata.Add(MetadataReference.CreateFromFile(reference));
        var compilation = CSharpCompilation.Create(assemblyName,
            sources.Select(source => CSharpSyntaxTree.ParseText(SourceText.From(source.Source, Encoding.UTF8), path: source.Document)),
            metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb")))
        {
            var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-razor-build-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
