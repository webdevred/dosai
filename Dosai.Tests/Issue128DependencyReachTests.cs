using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #128: the reach walk of --dependency-flows reachable keyed a member by the name of its
// declaring type. A nested type has no namespace of its own, so the <>c of every assembly was
// one key, and a package nothing calls was analyzed because a called one also has a lambda.
public sealed class Issue128DependencyReachTests
{
    private const string LambdaInTheCalledPackage = """
        using System.Linq;

        namespace LibA;

        public static class A
        {
            public static int Run(int[] values) => values.Where(value => value > 0).Sum();
        }
        """;

    private const string LambdaInThePackageNothingCalls = """
        using System.Linq;

        namespace LibB;

        public static class B
        {
            public static void NeverCalled(string[] inputs)
            {
                _ = inputs.Where(input => input.Length > 0).ToArray();
                System.Diagnostics.Process.Start(System.Console.ReadLine()!);
            }
        }
        """;

    private const string AppCallingTheLambdaPackage = """
        namespace App;

        public static class Program
        {
            public static void Main(string[] args) => System.Console.WriteLine(LibA.A.Run(new[] { args.Length }));
        }
        """;

    private const string NestedTypeInTheCalledPackage = """
        namespace LibA;

        public static class A
        {
            public static void Run(string value) => Options.Apply(value);

            public static class Options
            {
                public static void Apply(string value) => System.Console.WriteLine(value);
            }
        }
        """;

    private const string NestedTypeInThePackageNothingCalls = """
        namespace LibB;

        public static class B
        {
            public static class Options
            {
                public static void Apply(string value) => System.Diagnostics.Process.Start(System.Console.ReadLine()!);
            }
        }
        """;

    private const string AppCallingTheNestedTypePackage = """
        namespace App;

        public static class Program
        {
            public static void Main(string[] args) => LibA.A.Run(args[0]);
        }
        """;

    private const string PackageWithNestedTypes = """
        using System.Linq;

        namespace Vendor;

        public static class Plain
        {
            public static int PlainLambda(int[] items) => items.Count(item =>
            {
                System.Diagnostics.Process.Start(System.Console.ReadLine()!);
                return true;
            });
        }

        public sealed class Cache<T>
        {
            public int GenericLambda(T[] items) => items.Count(item =>
            {
                System.Diagnostics.Process.Start(System.Console.ReadLine()!);
                return true;
            });
        }

        public static class Outer
        {
            public static class Inner
            {
                public static void NestedRun() => System.Diagnostics.Process.Start(System.Console.ReadLine()!);
            }
        }

        public static class Box<T>
        {
            public static class Inner
            {
                public static void GenericNestedRun() => System.Diagnostics.Process.Start(System.Console.ReadLine()!);
            }
        }
        """;

    private const string AppCallingEveryNestedType = """
        namespace App;

        public static class Program
        {
            public static void Main()
            {
                Vendor.Plain.PlainLambda(new[] { 1 });
                new Vendor.Cache<int>().GenericLambda(new[] { 1 });
                Vendor.Outer.Inner.NestedRun();
                Vendor.Box<int>.Inner.GenericNestedRun();
            }
        }
        """;

    [Fact]
    public void DataFlows_ReachableDependencyFlows_SkipAPackageThatOnlySharesLambdaNamesWithACalledOne()
    {
        using var output = BuildOutput(AppCallingTheLambdaPackage, ("LibA", LambdaInTheCalledPackage), ("LibB", LambdaInThePackageNothingCalls));

        var all = Analyze(output.Path, DependencyFlowMode.All);
        var reachable = Analyze(output.Path, DependencyFlowMode.Reachable);

        Assert.Equal(["NeverCalled"], MethodsWithSinks(all, "LibB.dll"));
        Assert.Empty(MethodsWithSinks(reachable, "LibB.dll"));
        Assert.Equal("4 of 8", SkippedDependencyBodies(reachable));
    }

    [Fact]
    public void DataFlows_ReachableDependencyFlows_SkipANestedTypeThatOnlySharesItsNameWithACalledOne()
    {
        using var output = BuildOutput(AppCallingTheNestedTypePackage, ("LibA", NestedTypeInTheCalledPackage), ("LibB", NestedTypeInThePackageNothingCalls));

        var all = Analyze(output.Path, DependencyFlowMode.All);
        var reachable = Analyze(output.Path, DependencyFlowMode.Reachable);

        Assert.Equal(["Apply"], MethodsWithSinks(all, "LibB.dll"));
        Assert.Empty(MethodsWithSinks(reachable, "LibB.dll"));
        Assert.Equal("1 of 3", SkippedDependencyBodies(reachable));
    }

    [Fact]
    public void DataFlows_ReachableDependencyFlows_KeepNestedTypeMembersHoweverTheCallNamesThem()
    {
        using var output = BuildOutput(AppCallingEveryNestedType, ("Vendor", PackageWithNestedTypes));

        var all = Analyze(output.Path, DependencyFlowMode.All);
        var reachable = Analyze(output.Path, DependencyFlowMode.Reachable);

        Assert.Equal(["<GenericLambda>b__0_0", "<PlainLambda>b__0_0", "GenericNestedRun", "NestedRun"], MethodsWithSinks(all, "Vendor.dll"));
        Assert.Equal(MethodsWithSinks(all, "Vendor.dll"), MethodsWithSinks(reachable, "Vendor.dll"));
        Assert.Null(SkippedDependencyBodies(reachable));
    }

    private static DataFlowResult Analyze(string path, DependencyFlowMode mode)
    {
        using var scope = DependencyFlowScope.Apply(mode);
        return DataFlowAnalyzer.Analyze(path);
    }

    private static List<string?> MethodsWithSinks(DataFlowResult result, string assemblyFileName) => result.Nodes
        .Where(node => node.IsSink && TransparencyBuilder.IsDependencyNode(node))
        .Where(node => node.Properties.TryGetValue("assembly", out var assembly) && assembly == assemblyFileName)
        .Select(node => node.MethodName)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    private static string? SkippedDependencyBodies(DataFlowResult result) => result.Diagnostics
        .Where(line => line.Contains(" method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal))
        .Select(line => line[..line.IndexOf(" method bodies", StringComparison.Ordinal)])
        .SingleOrDefault();

    private static TempDir BuildOutput(string appSource, params (string Name, string Source)[] packages)
    {
        var output = new TempDir();
        var references = packages
            .Select(package => EmitFixture.Emit(output.Path, package.Name, package.Source, $"/_/{package.Name}/{package.Name}.cs"))
            .ToArray();
        EmitFixture.Emit(output.Path, "App", appSource, "/_/App/Program.cs", references);
        File.WriteAllText(Path.Combine(output.Path, "App.deps.json"), DepsJson(packages.Select(package => package.Name)));
        return output;
    }

    private static string DepsJson(IEnumerable<string> packages)
    {
        var targets = new Dictionary<string, object> { ["App/1.0.0"] = new { runtime = new Dictionary<string, object> { ["App.dll"] = new { } } } };
        var libraries = new Dictionary<string, object> { ["App/1.0.0"] = new { type = "project" } };
        foreach (var name in packages)
        {
            targets[$"{name}/1.0.0"] = new { runtime = new Dictionary<string, object> { [$"lib/net8.0/{name}.dll"] = new { } } };
            libraries[$"{name}/1.0.0"] = new { type = "package" };
        }

        return JsonSerializer.Serialize(new { targets = new Dictionary<string, object> { [".NETCoreApp,Version=v8.0"] = targets }, libraries });
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue128-" + Guid.NewGuid().ToString("N"));

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
