using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RevitProjectBrain.Scripts;

namespace OctopusSkill;

public static class SkillCodeCheck
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);
    private static readonly string[] RevitUsings =
    [
        "Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Mechanical",
        "Autodesk.Revit.DB.Plumbing", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI", "Autodesk.Revit.UI.Selection"
    ];

    public static IReadOnlyList<string> Check(string body, string? revitApi)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>().ToList();
        var full = revitApi is not null;
        if (full)
        {
            foreach (var file in new[] { "RevitAPI.dll", "RevitAPIUI.dll" })
            {
                var path = Path.Combine(revitApi!, file);
                if (!File.Exists(path)) return new[] { "Missing Revit API assembly: " + file };
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }
        var context = full ? """
            public sealed class OctopusCheckContext
            {
                public Autodesk.Revit.DB.Document Doc => null!;
                public Autodesk.Revit.UI.UIDocument UiDoc => null!;
                public Autodesk.Revit.DB.View View => null!;
                public System.Text.Json.JsonElement Input => default;
                public void Log(string text) { }
            }
            """ : """
            public sealed class OctopusCheckContext
            {
                public object Doc => null!;
                public object UiDoc => null!;
                public object View => null!;
                public System.Text.Json.JsonElement Input => default;
                public void Log(string text) { }
            }
            """;
        var contextCompilation = CSharpCompilation.Create("OctopusCheckContext", new[] { CSharpSyntaxTree.ParseText(context, ParseOptions) },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var contextImage = new MemoryStream();
        var emitted = contextCompilation.Emit(contextImage);
        if (!emitted.Success)
            return emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage()).ToArray();
        references.Add(MetadataReference.CreateFromImage(contextImage.ToArray()));
        var result = ScriptCompiler.Compile(body, "OctopusCheckContext", full ? RevitUsings : Array.Empty<string>(), references);
        if (full)
            return result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Line}:{d.Column} {d.Id}: {d.Message}")
                .Concat(result.Violations.Select(v => $"{v.Line}:{v.Column} {v.Rule}: {v.Symbol}")).Distinct().ToArray();

        // Unresolved Autodesk symbols are expected here. Syntax and bound System symbols still get checked.
        var header = "using System;\nusing System.Linq;\nusing System.Collections.Generic;\n" +
            "public static class OctopusScript\n{\n    public static object? Run(OctopusCheckContext ctx)\n    {\n";
        var tree = CSharpSyntaxTree.ParseText(header + body + "\n    }\n}\n", ParseOptions);
        var root = tree.GetCompilationUnitRoot();
        var problems = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
        {
            var start = tree.GetText().Lines.GetLinePosition(header.Length);
            var point = tree.GetText().Lines.GetLinePosition(Math.Clamp(d.Location.SourceSpan.Start, header.Length, header.Length + body.Length));
            return $"{point.Line - start.Line + 1}:{point.Character + 1} {d.Id}: {d.GetMessage()}";
        }).ToList();
        if (root.Members is not [ClassDeclarationSyntax type] ||
            type.Members is not [MethodDeclarationSyntax { Body: { } block }] ||
            block.OpenBraceToken.SpanStart != header.Length - 2 ||
            block.CloseBraceToken.SpanStart != header.Length + body.Length + 5 ||
            type.CloseBraceToken.SpanStart != header.Length + body.Length + 7 ||
            block.CloseBraceToken.IsMissing || type.CloseBraceToken.IsMissing)
            problems.Add("Supply only the Run method body.");
        problems.AddRange(result.Violations.Where(v => v.Rule is not ("DocumentLifecycle" or "Transaction"))
            .Select(v => $"{v.Line}:{v.Column} {v.Rule}: {v.Symbol}"));
        return problems.Distinct().ToArray();
    }
}
