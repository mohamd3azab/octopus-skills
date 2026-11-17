using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace RevitProjectBrain.Scripts;

public sealed record ScriptPolicyViolation(string Rule, string Symbol, int Line, int Column);

public static class ScriptPolicy
{
    public static IReadOnlyList<ScriptPolicyViolation> Check(SemanticModel model, TextSpan body)
    {
        var root = model.SyntaxTree.GetRoot();
        var violations = new List<ScriptPolicyViolation>();
        foreach (var token in root.DescendantTokens().Where(t => body.Contains(t.Span)))
        {
            if (token.IsKind(SyntaxKind.UnsafeKeyword))
                Add("UnsafeCode", "unsafe", token.SpanStart);
            if (token.IsKind(SyntaxKind.ExternKeyword))
                Add("NativeInterop", "extern", token.SpanStart);
        }

        foreach (var node in root.DescendantNodes(n => n.FullSpan.IntersectsWith(body)))
        {
            if (!body.Contains(node.Span)) continue;
            var (symbol, position) = node switch
            {
                SimpleNameSyntax name => (model.GetSymbolInfo(name).Symbol, name.SpanStart),
                ImplicitObjectCreationExpressionSyntax creation => (model.GetSymbolInfo(creation).Symbol, creation.NewKeyword.SpanStart),
                ObjectCreationExpressionSyntax creation => (model.GetSymbolInfo(creation).Symbol, creation.Type.SpanStart),
                _ => (null, 0)
            };
            if (symbol is null) continue;
            var type = symbol as ITypeSymbol ?? symbol.ContainingType;
            if (type is null) continue;
            var rule = Rule(symbol, type, node, model);
            if (rule is not null)
            {
                // A construction may bind both its type name and its constructor.
                var display = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && rule != "FileWrite"
                    ? type.ToDisplayString() : symbol.ToDisplayString();
                Add(rule, display, position);
            }
        }

        return violations.Distinct().OrderBy(v => v.Line).ThenBy(v => v.Column).ThenBy(v => v.Rule).ToArray();

        void Add(string rule, string symbol, int position)
        {
            var (line, column) = ScriptCompiler.BodyPosition(model.SyntaxTree, body, position);
            violations.Add(new(rule, symbol, line, column));
        }
    }

    private static string? Rule(ISymbol symbol, ITypeSymbol type, SyntaxNode node, SemanticModel model)
    {
        // dynamic is bound at run time, so the checks below cannot see what it calls (for example Document.Save).
        if (type.TypeKind == TypeKind.Dynamic) return "Dynamic";
        var owner = type.OriginalDefinition.ToDisplayString();
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        // Reflection reaches any member by name, past every rule here. Reading names (Type.Name) stays allowed;
        // calling through reflection (Invoke, SetValue, Assembly.Load...) does not.
        if (InNamespace(ns, "System.Reflection") && symbol is IMethodSymbol { MethodKind: not MethodKind.PropertyGet } ||
            InNamespace(ns, "System.Reflection.Emit") || owner is "System.Activator" or "System.AppDomain" ||
            owner == "System.Delegate" && symbol.Name == "DynamicInvoke" ||
            owner == "System.Type" && symbol.Name is "GetType" or "InvokeMember" or "GetMethod" or "GetMethods" or "GetProperty" or
                "GetProperties" or "GetField" or "GetFields" or "GetMember" or "GetMembers" or "GetConstructor" or "GetConstructors" or "GetEvent")
            return "Reflection";
        if (InNamespace(ns, "System.Net")) return "Network";
        if (owner == "System.Diagnostics.Process") return "Process";
        if (InNamespace(ns, "System.Reflection.Emit") ||
            owner == "System.Reflection.Assembly" && symbol.Name.StartsWith("Load", StringComparison.Ordinal))
            return "Reflection";
        if (InNamespace(ns, "System.Runtime.InteropServices")) return "NativeInterop";
        if (owner is "System.Threading.Thread" or "System.Threading.ThreadPool" ||
            owner == "System.Threading.Tasks.Task" && symbol.Name == "Run")
            return "BackgroundExecution";
        if (owner == "System.Environment" && symbol.Name is "Exit" or "SetEnvironmentVariable")
            return "Environment";
        if (type.Name is "Transaction" or "TransactionGroup" or "SubTransaction")
            return "Transaction";
        if (type.Name is "Document" or "UIDocument" && symbol.Name is "Save" or "SaveAs" or "Close" or "SynchronizeWithCentral" ||
            owner == "Autodesk.Revit.UI.UIApplication" && symbol.Name == "OpenAndActivateDocument")
            return "DocumentLifecycle";
        return IsFileWrite(owner, symbol, node, model) ? "FileWrite" : null;
    }

    private static bool InNamespace(string actual, string root) =>
        actual == root || actual.StartsWith(root + ".", StringComparison.Ordinal);

    private static bool IsFileWrite(string owner, ISymbol symbol, SyntaxNode node, SemanticModel model)
    {
        if (symbol is IPropertySymbol)
        {
            if (owner is not ("System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileSystemInfo")) return false;
            var target = node.Parent is MemberAccessExpressionSyntax access && access.Name == node ? access : node;
            return target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target;
        }
        if (symbol is not IMethodSymbol method) return false;
        var name = method.Name;
        if (owner == "System.IO.FileStream" && method.MethodKind == MethodKind.Constructor ||
            owner is "System.IO.File" or "System.IO.FileInfo" && name is "Open" or "OpenHandle")
            return !IsReadOnlyOpen(node, model);
        if (owner == "System.IO.StreamWriter" && method.MethodKind == MethodKind.Constructor) return true;
        if (owner is not ("System.IO.File" or "System.IO.Directory" or "System.IO.FileStream" or
            "System.IO.StreamWriter" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.Stream" or "System.IO.TextWriter"))
            return false;

        return name.StartsWith("Write", StringComparison.Ordinal) || name.StartsWith("Append", StringComparison.Ordinal) ||
            name.StartsWith("Create", StringComparison.Ordinal) || name.StartsWith("Set", StringComparison.Ordinal) ||
            name is "Delete" or "Move" or "MoveTo" or "Replace" or "Copy" or "CopyTo" or "OpenWrite" or
                "Encrypt" or "Decrypt" or "FlushAsync" ||
            name == "Flush" && owner is "System.IO.FileStream" or "System.IO.StreamWriter" ||
            name == "CreateSubdirectory";
    }

    private static bool IsReadOnlyOpen(SyntaxNode node, SemanticModel model)
    {
        var call = node.Parent is MemberAccessExpressionSyntax member && member.Name == node ? member : node;
        if (call.Parent is InvocationExpressionSyntax invocation && invocation.Expression == call) call = invocation;
        var arguments = model.GetOperation(call) switch
        {
            IInvocationOperation operation => operation.Arguments,
            IObjectCreationOperation operation => operation.Arguments,
            _ => default
        };
        if (arguments.IsDefault) return false;
        return arguments.Any(a => a.Parameter?.Type.ToDisplayString() == "System.IO.FileMode" &&
                a.Value.ConstantValue is { HasValue: true, Value: (int)FileMode.Open }) &&
            arguments.Any(a => a.Parameter?.Type.ToDisplayString() == "System.IO.FileAccess" &&
                a.Value.ConstantValue is { HasValue: true, Value: (int)FileAccess.Read }) &&
            !arguments.Any(a => a.Parameter?.Type.ToDisplayString() == "System.IO.FileOptions" &&
                !(a.Value.ConstantValue is { HasValue: true, Value: int flags } && (flags & (int)FileOptions.DeleteOnClose) == 0));
    }
}
