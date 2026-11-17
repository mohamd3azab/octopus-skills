using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RevitProjectBrain;

namespace OctopusSkill;

public static class SkillCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length < 2) throw new InvalidDataException("Usage: octoskill validate|pack|hash|sign|verify|index <path> [options]");
            var options = Options(args.Skip(2).ToArray());
            var allowed = args[0] switch
            {
                "validate" => new[] { "--revit-api" },
                "pack" => new[] { "-o" },
                "hash" => Array.Empty<string>(),
                "sign" => new[] { "--key-env" },
                "verify" => new[] { "--signature", "--public-key" },
                "index" => new[] { "-o", "--sign-key-env" },
                _ => throw new InvalidDataException("Unknown octoskill command.")
            };
            if (options.Keys.Except(allowed).Any()) throw new InvalidDataException("Unknown option for " + args[0] + ".");
            string Required(string name) => options.GetValueOrDefault(name) ?? throw new InvalidDataException("Missing " + name + ".");
            switch (args[0])
            {
                case "validate":
                {
                    var result = Read(args[1]);
                    var problems = result.Problems.ToList();
                    var api = options.GetValueOrDefault("--revit-api");
                    output.WriteLine(api is null
                        ? "Syntax check and System-level policy only; Autodesk rules and full compilation require --revit-api. Octopus checks again before running."
                        : "Full compilation and policy with the supplied Revit API metadata.");
                    if (result.Success) problems.AddRange(SkillCodeCheck.Check(result.Package!.Script, api));
                    foreach (var problem in problems) error.WriteLine(problem);
                    if (problems.Count == 0) output.WriteLine("Valid.");
                    return problems.Count == 0 ? 0 : 1;
                }
                case "pack":
                {
                    if (!Directory.Exists(args[1])) throw new InvalidDataException("pack requires a skill folder.");
                    var package = Require(Read(args[1]));
                    WritePackage(Required("-o"), package);
                    output.WriteLine(package.ContentHash);
                    return 0;
                }
                case "hash":
                    output.WriteLine(Require(SkillPackageReader.Read(args[1])).ContentHash);
                    return 0;
                case "sign":
                    output.WriteLine(SkillSigning.Sign(Require(SkillPackageReader.Read(args[1])), Required("--key-env")));
                    return 0;
                case "verify":
                {
                    var valid = SkillSigning.Verify(Require(SkillPackageReader.Read(args[1])),
                        Required("--signature"), File.ReadAllText(Required("--public-key")));
                    output.WriteLine(valid ? "Signature valid." : "Signature invalid.");
                    return valid ? 0 : 1;
                }
                case "index":
                    Index(args[1], Required("-o"), options.GetValueOrDefault("--sign-key-env"));
                    output.WriteLine("Catalog index written.");
                    return 0;
                default: throw new InvalidDataException("Unknown command.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or CryptographicException or JsonException or FormatException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith('-') || args[i + 1].StartsWith("--") ||
                !options.TryAdd(args[i], args[i + 1]))
                throw new InvalidDataException("Options require a unique name and value.");
        }
        return options;
    }

    public static SkillPackageResult Read(string path)
    {
        if (!Directory.Exists(path)) return SkillPackageReader.Read(path);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var problems = new List<string>();
        Visit(path);
        var result = SkillPackageReader.FromEntries(entries, problems);
        if (!result.Success) return result;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in result.Package!.Entries)
            {
                using var writer = zip.CreateEntry(entry.Key, CompressionLevel.Optimal).Open();
                writer.Write(entry.Value);
            }
        return stream.Length <= SkillPackageReader.MaxFileBytes ? result :
            new(null, new[] { "The package file exceeds 1 MB." });

        void Visit(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                problems.Add("Symbolic links are not allowed: " + directory);
                return;
            }
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    problems.Add("Symbolic links are not allowed: " + file);
                    continue;
                }
                if (Directory.Exists(file)) { Visit(file); continue; }
                var name = Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/');
                if (entries.Count >= SkillPackageReader.MaxEntries)
                {
                    problems.Add("The package has more than 50 entries.");
                    continue;
                }
                if (new FileInfo(file).Length > SkillPackageReader.MaxEntryBytes)
                {
                    problems.Add("Entry exceeds 256 KB: " + name);
                    continue;
                }
                entries.Add(name, File.ReadAllBytes(file));
            }
        }
    }

    private static SkillPackage Require(SkillPackageResult result) =>
        result.Success ? result.Package! : throw new InvalidDataException(string.Join(Environment.NewLine, result.Problems));

    private static void WritePackage(string destination, SkillPackage package)
    {
        var path = Path.GetFullPath(destination);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            SkillPackageReader.WriteZip(temp, package.Entries);
            Require(SkillPackageReader.Read(temp));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Index(string root, string destination, string? keyVariable)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The skills root does not exist.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The skills root cannot be a symbolic link.");
        var packages = new List<SkillPackage>();
        var problems = new List<string>();
        foreach (var id in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(id) & FileAttributes.ReparsePoint) != 0) { problems.Add("Symbolic link: " + id); continue; }
            foreach (var version in Directory.EnumerateDirectories(id).Order(StringComparer.Ordinal))
            {
                var result = Read(version);
                if (!result.Success) { problems.AddRange(result.Problems.Select(p => version + ": " + p)); continue; }
                var package = result.Package!;
                if (package.Manifest.Id != Path.GetFileName(id) || package.Manifest.Version != Path.GetFileName(version))
                    problems.Add(version + ": folder id/version does not match the manifest.");
                problems.AddRange(SkillCodeCheck.Check(package.Script, null).Select(p => version + ": " + p));
                packages.Add(package);
            }
        }
        if (problems.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, problems));
        // Validate and sign every package before creating the publishable tree.
        var skills = packages.Select(p => new
        {
            p.Manifest.Id, p.Manifest.Name, p.Manifest.Version, p.Manifest.Author, p.Manifest.Description,
            p.Manifest.Family, p.Manifest.ChangesModel, p.Manifest.Revit, p.ContentHash,
            Signature = keyVariable is null ? null : SkillSigning.Sign(p, keyVariable),
            Package = $"packages/{p.Manifest.Id}-{p.Manifest.Version}.octoskill"
        }).ToArray();
        var target = Path.GetFullPath(destination);
        var staging = target + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(staging, "packages"));
        try
        {
            foreach (var package in packages)
                WritePackage(Path.Combine(staging, "packages", $"{package.Manifest.Id}-{package.Manifest.Version}.octoskill"), package);
            File.WriteAllText(Path.Combine(staging, "index.json"), JsonSerializer.Serialize(new
            {
                Format = 1, Generated = DateTimeOffset.UtcNow, Skills = skills
            }, SkillPackageReader.Json));
            if (!Directory.Exists(target)) Directory.Move(staging, target);
            else
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The site directory cannot be a symbolic link.");
                var packageFolder = Path.Combine(target, "packages");
                Directory.CreateDirectory(packageFolder);
                if ((File.GetAttributes(packageFolder) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The packages directory cannot be a symbolic link.");
                foreach (var file in Directory.EnumerateFiles(Path.Combine(staging, "packages")))
                    File.Move(file, Path.Combine(packageFolder, Path.GetFileName(file)), overwrite: true);
                File.Move(Path.Combine(staging, "index.json"), Path.Combine(target, "index.json"), overwrite: true);
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}

public static class SkillSigning
{
    private static byte[] Payload(SkillPackage package) =>
        Encoding.UTF8.GetBytes($"octoskill-v1:{package.Manifest.Id}@{package.Manifest.Version}:{package.ContentHash}");

    public static string Sign(SkillPackage package, string environmentVariable)
    {
        var pem = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(pem)) throw new InvalidDataException("The signing key environment variable is missing or empty.");
        var fields = PemEncoding.Find(pem);
        if (!pem.AsSpan()[fields.Label].SequenceEqual("PRIVATE KEY"))
            throw new InvalidDataException("Signing requires a PKCS#8 PRIVATE KEY PEM in the named environment variable.");
        var bytes = Convert.FromBase64String(pem[fields.Base64Data]);
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out var read);
            if (read != bytes.Length) throw new CryptographicException("Unexpected trailing private key bytes.");
            CheckCurve(key);
            return Convert.ToBase64String(key.SignData(Payload(package), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool Verify(SkillPackage package, string signature, string publicPem)
    {
        try
        {
            var fields = PemEncoding.Find(publicPem);
            if (!publicPem.AsSpan()[fields.Label].SequenceEqual("PUBLIC KEY")) return false;
            using var key = ECDsa.Create();
            var bytes = Convert.FromBase64String(publicPem[fields.Base64Data]);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) return false;
            CheckCurve(key);
            var decoded = Convert.FromBase64String(signature);
            return decoded.Length == 64 && key.VerifyData(Payload(package), decoded, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException) { return false; }
    }

    private static void CheckCurve(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("The signing key must use ECDSA P-256.");
    }
}
