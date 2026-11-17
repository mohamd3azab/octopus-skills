using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace RevitProjectBrain.Scripts;

/// <summary>
/// What the add-in calls across the AssemblyLoadContext boundary. Only framework types cross it (strings, string
/// arrays, byte arrays), so no Roslyn type ever reaches the add-in's context.
/// </summary>
public static class ScriptCompilerEntry
{
    // Reading a reference assembly costs time; Revit's and the framework's do not change during a session.
    private static readonly ConcurrentDictionary<string, MetadataReference> References = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compiles and checks a script body. Returns JSON: {"ok":bool,"errors":[{line,column,id,message}],
    /// "violations":[{rule,symbol,line,column}],"warnings":n}. <paramref name="image"/> holds the assembly when ok.
    /// </summary>
    public static string Compile(string body, string contextTypeName, string[] usings, string[] referencePaths, out byte[]? image)
    {
        var references = referencePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => References.GetOrAdd(path, p => MetadataReference.CreateFromFile(p))).ToArray();
        var result = ScriptCompiler.Compile(body, contextTypeName, usings, references);
        image = result.AssemblyImage;
        return JsonSerializer.Serialize(new
        {
            ok = result.AssemblyImage is not null,
            errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new { line = d.Line, column = d.Column, id = d.Id, message = d.Message }).Take(30),
            violations = result.Violations.Select(v => new { rule = v.Rule, symbol = v.Symbol, line = v.Line, column = v.Column }).Take(30),
            warnings = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
        });
    }
}
