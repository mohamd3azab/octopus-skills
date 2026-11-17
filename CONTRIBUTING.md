# Contributing a skill

Use your AI app with Octopus MCP to develop a small C# method body against an open model. Test it through Octopus's query or modify workflow, review its effects and approval preview, and keep its code and limitations. Domain counts, room associations and quantities continue to use programmed Octopus skills.

Add a new, unpacked version:

    skills/acme.example/1.0.0/
      skill.json
      script.cs
      README.md
      examples/default.json

The script is the body of static object? Run(ScriptContext ctx). Use ctx.Doc, ctx.UiDoc, ctx.View and ctx.Log; skill inputs use ctx.Input in the client integration. Return JSON-serializable values. Octopus owns the transaction. Scripts cannot save, sync, close or open documents, create transactions, write files, access networks or processes, invoke reflection, or start background work. The policy is a best-effort check; review the source and test it in Revit.

The format-1 manifest contains format, id, name, version, author, description, family, changesModel, inputSchema, entry and revit. Use an id of 3–64 lowercase letters, digits, hyphens or dots, starting with a letter and containing no double dots. A publisher prefix is recommended. The folder id and version must match the manifest. Versions use digits in MAJOR.MINOR.PATCH. Families are Read, Annotation, Modeling and Generating. Name and author are nonblank and at most 80 characters; description is nonblank and at most 600. inputSchema is an object with type "object"; entry is script.cs; revit is a nonempty list of four-digit years.

Packages allow at most 50 files and 1 MB compressed; each entry is at most 256 KB. Only .json, .cs and .md files are allowed. Paths must be relative, without backslashes or double dots; nested archives are refused. Every example must be valid JSON, an object, and contain every top-level required input name. Format 1 does not validate further JSON Schema constraints. Write a README describing inputs, intended use, limitations and Revit testing.

Run validation before submitting:

    dotnet tools/OctopusSkill/bin/Release/net8.0/octoskill.dll validate skills/acme.example/1.0.0
    dotnet tools/OctopusSkill/bin/Release/net8.0/octoskill.dll validate skills/acme.example/1.0.0 --revit-api "/path/to/Revit"

Open a pull request with the unpacked files so the owner can read the script. Once a version is on main, create a new version to change it. The PR check refuses edits or removal of published versions. Validation without Revit is limited to syntax and System-level policy. The owner reviews the source and Revit behavior before merge. Publishing after merge signs the package hash and identity using the repository secret. Contributors never supply private keys or review flags. Every execution remains subject to Octopus approval and logging.

Pull requests may only add new skills/<id>/<version>/ folders. Tool and workflow changes come only from maintainers on main.
