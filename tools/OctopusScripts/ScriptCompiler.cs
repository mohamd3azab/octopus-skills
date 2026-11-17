using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RevitProjectBrain.Scripts;

public sealed record ScriptDiagnostic(DiagnosticSeverity Severity, string Id, string Message, int Line, int Column);

public sealed record ScriptCompilationResult(
    byte[]? AssemblyImage,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    IReadOnlyList<ScriptPolicyViolation> Violations);

public static class ScriptCompiler
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    public static ScriptCompilationResult Compile(string body, string contextTypeName,
        IEnumerable<string> extraUsings, IEnumerable<MetadataReference> references)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextTypeName);
        var context = SyntaxFactory.ParseName(contextTypeName);
        if (context.ContainsDiagnostics || context.ContainsDirectives || context.DescendantTrivia().Any())
            throw new ArgumentException("Supply a C# context type name.", nameof(contextTypeName));

        var usings = new List<string> { "System", "System.Linq", "System.Collections.Generic" };
        foreach (var name in extraUsings)
        {
            var syntax = SyntaxFactory.ParseName(name);
            if (syntax.ContainsDiagnostics || syntax.ContainsDirectives || syntax.DescendantTrivia().Any())
                throw new ArgumentException("Supply namespace names for extra usings.", nameof(extraUsings));
            if (!usings.Contains(name, StringComparer.Ordinal)) usings.Add(name);
        }

        var header = string.Concat(usings.Select(name => $"using {name};\n")) +
            $"public static class OctopusScript\n{{\n    public static object? Run({context} ctx)\n    {{\n";
        var bodySpan = new TextSpan(header.Length, body.Length);
        var tree = CSharpSyntaxTree.ParseText(header + body + "\n    }\n}\n", ParseOptions);
        var compilation = CSharpCompilation.Create($"OctopusScript_{Guid.NewGuid():N}", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var diagnostics = compilation.GetDiagnostics().Select(d => ToDiagnostic(d, tree, bodySpan)).ToArray();
        var root = tree.GetCompilationUnitRoot();

        // Keep the generated type and method fixed even if a body attempts to close and reopen them.
        if (root.Members is not [ClassDeclarationSyntax type] ||
            type.Members is not [MethodDeclarationSyntax { Body: { } block }] ||
            block.OpenBraceToken.SpanStart != header.Length - 2 ||
            block.CloseBraceToken.SpanStart != bodySpan.End + 5 ||
            type.CloseBraceToken.SpanStart != bodySpan.End + 7 ||
            block.CloseBraceToken.IsMissing || type.CloseBraceToken.IsMissing)
        {
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new(null, diagnostics, Array.Empty<ScriptPolicyViolation>());
            var diagnostic = new ScriptDiagnostic(DiagnosticSeverity.Error, "OCT001", "Supply only the Run method body.", 1, 1);
            return new(null, diagnostics.Append(diagnostic).ToArray(), Array.Empty<ScriptPolicyViolation>());
        }

        var violations = ScriptPolicy.Check(compilation.GetSemanticModel(tree), bodySpan);
        if (violations.Count > 0 || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new(null, diagnostics, violations);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        var emittedDiagnostics = emit.Diagnostics.Select(d => ToDiagnostic(d, tree, bodySpan));
        return new(emit.Success ? stream.ToArray() : null,
            diagnostics.Concat(emittedDiagnostics).Distinct().ToArray(), violations);
    }

    internal static (int Line, int Column) BodyPosition(SyntaxTree tree, TextSpan body, int position)
    {
        var lines = tree.GetText().Lines;
        var start = lines.GetLinePosition(body.Start);
        var point = lines.GetLinePosition(Math.Clamp(position, body.Start, body.End));
        return (point.Line - start.Line + 1, point.Character + 1);
    }

    private static ScriptDiagnostic ToDiagnostic(Diagnostic diagnostic, SyntaxTree tree, TextSpan body)
    {
        var (line, column) = diagnostic.Location.SourceTree == tree
            ? BodyPosition(tree, body, diagnostic.Location.SourceSpan.Start) : (1, 1);
        return new(diagnostic.Severity, diagnostic.Id, diagnostic.GetMessage(), line, column);
    }
}
