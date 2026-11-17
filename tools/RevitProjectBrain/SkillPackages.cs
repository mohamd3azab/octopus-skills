using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RevitProjectBrain;

public sealed record SkillManifest(
    int Format,
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Family,
    bool ChangesModel,
    JsonElement InputSchema,
    string Entry,
    IReadOnlyList<string> Revit)
{
    public static IReadOnlyList<string> Validate(SkillManifest? manifest)
    {
        if (manifest is null) return new[] { "The manifest must be a JSON object." };
        var problems = new List<string>();
        if (manifest.Format != 1) problems.Add("format must be 1.");
        if (manifest.Id is null || !Regex.IsMatch(manifest.Id, @"\A[a-z][a-z0-9.-]{2,63}\z") || manifest.Id.Contains(".."))
            problems.Add("id must be 3–64 lowercase letters, digits, hyphens or dots, start with a letter and contain no '..'.");
        if (manifest.Version is null || !Regex.IsMatch(manifest.Version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z"))
            problems.Add("version must be MAJOR.MINOR.PATCH using digits only.");
        if (manifest.Family is not ("Read" or "Annotation" or "Modeling" or "Generating"))
            problems.Add("family must be Read, Annotation, Modeling or Generating.");
        Required(manifest.Name, "name", 80);
        Required(manifest.Description, "description", 600);
        Required(manifest.Author, "author", 80);
        if (manifest.InputSchema.ValueKind != JsonValueKind.Object ||
            !manifest.InputSchema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
            problems.Add("inputSchema must be a JSON object with type 'object'.");
        if (manifest.Entry != "script.cs") problems.Add("entry must be script.cs.");
        if (manifest.Revit is null || manifest.Revit.Count == 0 ||
            manifest.Revit.Any(year => year is null || !Regex.IsMatch(year, @"\A[0-9]{4}\z")))
            problems.Add("revit must contain at least one four-digit year.");
        return problems;

        void Required(string? value, string field, int limit)
        {
            if (string.IsNullOrWhiteSpace(value)) problems.Add($"{field} is required and must be non-blank.");
            else if (value.Length > limit) problems.Add($"{field} must be at most {limit} characters.");
        }
    }
}

public sealed record SkillPackage(
    SkillManifest Manifest,
    string Script,
    string? Readme,
    IReadOnlyDictionary<string, string> Examples,
    string ContentHash,
    IReadOnlyDictionary<string, byte[]> Entries);

public sealed record SkillPackageResult(SkillPackage? Package, IReadOnlyList<string> Problems)
{
    public bool Success => Package is not null && Problems.Count == 0;
}

public static class SkillPackageReader
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxEntries = 50;
    public const int MaxEntryBytes = 256 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static SkillPackageResult Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadStream(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, new[] { $"Cannot read package: {ex.Message}" });
        }
    }

    public static SkillPackageResult Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadStream(stream);
    }

    private static SkillPackageResult ReadStream(Stream stream)
    {
        try
        {
            var problems = new List<string>();
            if (stream.Length > MaxFileBytes) problems.Add("The package file exceeds 1 MB.");
            // Oversized archives are never opened or expanded.
            if (problems.Count > 0) return new(null, problems);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) problems.Add("The package has more than 50 entries.");
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (var entry in zip.Entries)
            {
                var pathProblems = PathProblems(entry.FullName);
                problems.AddRange(pathProblems);
                var duplicate = !names.Add(entry.FullName);
                if (duplicate) problems.Add($"Duplicate package path: {entry.FullName}.");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    problems.Add($"Symbolic links are not allowed: {entry.FullName}.");
                if (entry.Length > MaxEntryBytes)
                {
                    problems.Add($"Entry exceeds 256 KB: {entry.FullName}.");
                    continue;
                }
                expandedBytes += entry.Length;
                if (expandedBytes > (long)MaxEntries * MaxEntryBytes)
                {
                    problems.Add("Expanded package exceeds the permitted entry budget.");
                    continue;
                }
                if (pathProblems.Count > 0 || duplicate) continue;
                try
                {
                    using var input = entry.Open();
                    using var output = new MemoryStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + count > MaxEntryBytes)
                            throw new InvalidDataException("Expanded entry exceeds 256 KB.");
                        output.Write(buffer, 0, count);
                    }
                    if (output.Length != entry.Length) throw new InvalidDataException("Entry length does not match its declared size.");
                    entries.Add(entry.FullName, output.ToArray());
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    problems.Add($"Cannot read {entry.FullName}: {ex.Message}");
                }
            }
            return FromEntries(entries, problems);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, new[] { $"Cannot read package: {ex.Message}" });
        }
    }

    public static string ContentHash(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Key));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(entry.Value);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static SkillPackageResult FromEntries(IReadOnlyDictionary<string, byte[]> entries, IEnumerable<string>? prior = null)
    {
        var problems = new List<string>(prior ?? Array.Empty<string>());
        if (entries.Count > MaxEntries) problems.Add("The package has more than 50 entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            problems.AddRange(PathProblems(entry.Key));
            if (!names.Add(entry.Key)) problems.Add($"Duplicate package path: {entry.Key}.");
            if (entry.Value.Length > MaxEntryBytes) problems.Add($"Entry exceeds 256 KB: {entry.Key}.");
            if (entry.Value.Length >= 4 && entry.Value[0] == 0x50 && entry.Value[1] == 0x4B &&
                ((entry.Value[2] == 3 && entry.Value[3] == 4) || (entry.Value[2] == 5 && entry.Value[3] == 6) ||
                    (entry.Value[2] == 7 && entry.Value[3] == 8)))
                problems.Add($"Nested archive: {entry.Key}.");
        }

        foreach (var path in entries.Keys)
        {
            var slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (names.Contains(path[..slash])) problems.Add($"Package path is both a file and a directory: {path[..slash]}.");
                slash = path.IndexOf('/', slash + 1);
            }
        }

        SkillManifest? manifest = null;
        if (!entries.TryGetValue("skill.json", out var manifestBytes)) problems.Add("Missing skill.json.");
        else
        {
            try
            {
                var json = Decode(manifestBytes);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var group in document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal))
                        if (group.Count() > 1) problems.Add($"Duplicate manifest field: {group.Key}.");
                    foreach (var field in new[] { "format", "id", "name", "version", "author", "description", "family", "changesModel", "inputSchema", "entry", "revit" })
                        if (!document.RootElement.TryGetProperty(field, out _)) problems.Add($"Missing manifest field: {field}.");
                    manifest = JsonSerializer.Deserialize<SkillManifest>(json, Json);
                }
                problems.AddRange(SkillManifest.Validate(manifest));
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                problems.Add("Invalid skill.json: " + ex.Message);
            }
        }

        string? script = null;
        if (!entries.TryGetValue("script.cs", out var scriptBytes)) problems.Add("Missing script.cs.");
        else script = Text("script.cs", scriptBytes);
        var readme = entries.TryGetValue("README.md", out var readmeBytes) ? Text("README.md", readmeBytes) : null;
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Key.StartsWith("examples/", StringComparison.Ordinal) &&
                     e.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Text(entry.Key, entry.Value);
            if (text is null) continue;
            try
            {
                using var example = JsonDocument.Parse(text);
                if (example.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"Example {entry.Key} must be a JSON object.");
                    continue;
                }
                if (manifest is not null && manifest.InputSchema.ValueKind == JsonValueKind.Object &&
                    manifest.InputSchema.TryGetProperty("required", out var required) &&
                    required.ValueKind == JsonValueKind.Array &&
                    required.EnumerateArray().All(name => name.ValueKind == JsonValueKind.String))
                {
                    foreach (var name in required.EnumerateArray())
                        if (!example.RootElement.TryGetProperty(name.GetString()!, out _))
                            problems.Add($"Example {entry.Key} is missing required property '{name.GetString()}'.");
                }
                examples.Add(entry.Key, text);
            }
            catch (JsonException ex) { problems.Add($"Invalid example JSON {entry.Key}: {ex.Message}"); }
        }
        if (problems.Count > 0) return new(null, problems.Distinct().ToArray());
        return new(new(manifest!, script!, readme, examples, ContentHash(entries),
            entries.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal)), Array.Empty<string>());

        string? Text(string path, byte[] bytes)
        {
            try { return Decode(bytes); }
            catch (DecoderFallbackException) { problems.Add($"Invalid UTF-8 text: {path}."); return null; }
        }
    }

    private static string Decode(byte[] bytes) => Utf8.GetString(bytes).TrimStart('\uFEFF');

    private static IReadOnlyList<string> PathProblems(string path)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains("..") || path.Contains('\\') ||
            path.Split('/').Any(part => part.Length == 0 || part == "." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(part.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase)))
            problems.Add($"Unsafe package path: {path}.");
        if (path.Equals("origin.json", StringComparison.OrdinalIgnoreCase))
            problems.Add("origin.json is library metadata and cannot be supplied by a package.");
        var extension = Path.GetExtension(path);
        if (!new[] { ".json", ".cs", ".md" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            problems.Add($"Disallowed extension or nested archive: {path}.");
        return problems;
    }

    internal static void WriteZip(string destination, IReadOnlyDictionary<string, byte[]> entries)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            using var output = zip.CreateEntry(entry.Key, CompressionLevel.Optimal).Open();
            output.Write(entry.Value);
        }
    }
}

public sealed record SkillOrigin(string Source, string? ImportedFrom, string ContentHash, DateTimeOffset SavedAt, bool Reviewed = false)
{
    public string? Signature { get; init; }
}
public sealed record SkillLibraryItem(SkillPackage Package, SkillOrigin Origin)
{
    public SkillManifest Manifest => Package.Manifest;
}

public sealed class SkillLibrary
{
    internal static readonly IComparer<string> VersionComparer = Comparer<string>.Create((left, right) =>
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var comparison = BigInteger.Parse(leftParts[i]).CompareTo(BigInteger.Parse(rightParts[i]));
            if (comparison != 0) return comparison;
        }
        return StringComparer.Ordinal.Compare(left, right);
    });

    public string Root { get; }
    public static SkillLibrary Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitProjectBrain", "skills"));

    private string StorageRoot => OperatingSystem.IsWindows() && !Root.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? Root.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + Root[2..] : @"\\?\" + Root
        : Root;

    public SkillLibrary(string root) => Root = Path.GetFullPath(root);

    public SkillPackageResult Save(SkillManifest manifest, string script, string? readme = null,
        IReadOnlyDictionary<string, string>? examples = null, bool overwrite = false)
    {
        var problems = SkillManifest.Validate(manifest);
        if (problems.Count > 0) return new(null, problems);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["skill.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, SkillPackageReader.Json),
            ["script.cs"] = Encoding.UTF8.GetBytes(script)
        };
        if (readme is not null) entries.Add("README.md", Encoding.UTF8.GetBytes(readme));
        foreach (var example in examples ?? new Dictionary<string, string>())
        {
            if (!example.Key.StartsWith("examples/", StringComparison.Ordinal) || !example.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return new(null, new[] { "Example paths must be examples/*.json." });
            entries.Add(example.Key, Encoding.UTF8.GetBytes(example.Value));
        }
        return Install(SkillPackageReader.FromEntries(entries), "local", null, overwrite);
    }

    public SkillPackageResult Import(string path, bool overwrite = false, string? signature = null) =>
        Install(SkillPackageReader.Read(path), "imported", Path.GetFileName(path), overwrite, signature);

    public IReadOnlyList<SkillLibraryItem> List()
    {
        if (!Directory.Exists(StorageRoot)) return Array.Empty<SkillLibraryItem>();
        EnsurePlain(StorageRoot);
        var items = new List<SkillLibraryItem>();
        foreach (var id in Directory.EnumerateDirectories(StorageRoot).Where(p => !Path.GetFileName(p).StartsWith('.')))
        {
            EnsurePlain(id);
            foreach (var version in Directory.EnumerateDirectories(id).Where(p => !Path.GetFileName(p).StartsWith('.')))
            {
                var item = ReadVersion(version);
                if (item is not null) items.Add(item);
            }
        }
        return items.OrderBy(i => i.Manifest.Id, StringComparer.Ordinal).ThenBy(i => i.Manifest.Version, VersionComparer).ToArray();
    }

    public SkillLibraryItem? Get(string id, string? version = null)
    {
        ValidateIdentity(id, version);
        if (version is not null) return ReadVersion(VersionPath(id, version));
        return List().Where(i => i.Manifest.Id == id)
            .OrderByDescending(i => i.Manifest.Version, VersionComparer).FirstOrDefault();
    }

    public void Remove(string id, string? version = null)
    {
        ValidateIdentity(id, version);
        if (!Directory.Exists(StorageRoot)) return;
        using var gate = Lock();
        var target = version is null ? Path.Combine(StorageRoot, id) : VersionPath(id, version);
        if (!Directory.Exists(target)) return;
        EnsureTreePlain(target);
        var removed = Path.Combine(StorageRoot, ".removed-" + Guid.NewGuid().ToString("N"));
        Directory.Move(target, removed);
        Directory.Delete(removed, recursive: true);
    }

    public void Export(string id, string path, string? version = null)
    {
        var item = Get(id, version) ?? throw new FileNotFoundException("The skill version is not installed.");
        var destination = Path.GetFullPath(path);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            SkillPackageReader.WriteZip(temp, item.Package.Entries);
            var validation = SkillPackageReader.Read(temp);
            if (!validation.Success) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Problems));
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private SkillPackageResult Install(SkillPackageResult result, string source, string? importedFrom, bool overwrite, string? signature = null)
    {
        if (!result.Success) return result;
        var package = result.Package!;
        // The ZIP size limit applies to locally assembled packages as well.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in package.Entries)
            {
                using var output = zip.CreateEntry(entry.Key, CompressionLevel.Optimal).Open();
                output.Write(entry.Value);
            }
        if (buffer.Length > SkillPackageReader.MaxFileBytes)
            return new(null, new[] { "The package file exceeds 1 MB." });

        using var gate = Lock();
        var destination = VersionPath(package.Manifest.Id, package.Manifest.Version);
        if (Directory.Exists(destination) && !overwrite)
            return new(null, new[] { $"Skill {package.Manifest.Id}@{package.Manifest.Version} already exists." });
        if (File.Exists(destination)) return new(null, new[] { "The version path is occupied by a file." });
        var staging = Path.Combine(StorageRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(StorageRoot, ".previous-" + Guid.NewGuid().ToString("N"));
        var movedPrevious = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var entry in package.Entries)
            {
                var path = Path.Combine(staging, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, entry.Value);
            }
            var origin = new SkillOrigin(source, importedFrom, package.ContentHash, DateTimeOffset.UtcNow) { Signature = signature };
            File.WriteAllText(Path.Combine(staging, "origin.json"), JsonSerializer.Serialize(origin, SkillPackageReader.Json));
            var publisher = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(publisher);
            EnsurePlain(publisher);
            if (Directory.Exists(destination))
            {
                EnsureTreePlain(destination);
                Directory.Move(destination, previous);
                movedPrevious = true;
            }
            try { Directory.Move(staging, destination); }
            catch
            {
                if (movedPrevious) Directory.Move(previous, destination);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        // Cleanup cannot turn a successfully installed version into a failed installation.
        if (movedPrevious)
        {
            try { Directory.Delete(previous, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(StorageRoot);
        EnsurePlain(StorageRoot);
        return new FileStream(Path.Combine(StorageRoot, ".library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private SkillLibraryItem? ReadVersion(string path)
    {
        if (!Directory.Exists(path)) return null;
        EnsureTreePlain(path);
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetRelativePath(path, file) != "origin.json").ToArray();
        if (files.Length > SkillPackageReader.MaxEntries) return null;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (new FileInfo(file).Length > SkillPackageReader.MaxEntryBytes) return null;
            entries.Add(Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(file));
        }
        var result = SkillPackageReader.FromEntries(entries);
        var originPath = Path.Combine(path, "origin.json");
        if (!result.Success || !File.Exists(originPath) || new FileInfo(originPath).Length > SkillPackageReader.MaxEntryBytes) return null;
        try
        {
            var origin = JsonSerializer.Deserialize<SkillOrigin>(File.ReadAllText(originPath), SkillPackageReader.Json);
            var manifest = result.Package!.Manifest;
            if (origin is null || origin.Source is not ("local" or "imported") ||
                origin.ContentHash != result.Package.ContentHash || Path.GetFileName(path) != manifest.Version ||
                Path.GetFileName(Path.GetDirectoryName(path)) != manifest.Id) return null;
            return new(result.Package, origin with { Reviewed = false });
        }
        catch (JsonException) { return null; }
    }

    private string VersionPath(string id, string version)
    {
        var parent = Path.Combine(StorageRoot, id);
        if (Directory.Exists(parent)) EnsurePlain(parent);
        return Path.Combine(parent, version);
    }

    private static void ValidateIdentity(string id, string? version)
    {
        if (!Regex.IsMatch(id, @"\A[a-z][a-z0-9.-]{2,63}\z") || id.Contains("..") ||
            version is not null && !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z"))
            throw new ArgumentException("Invalid skill id or version.");
    }

    private static void EnsurePlain(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Library paths cannot be symbolic links or reparse points.");
    }

    private static void EnsureTreePlain(string path)
    {
        EnsurePlain(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            EnsurePlain(entry);
            if (Directory.Exists(entry)) EnsureTreePlain(entry);
        }
    }
}
