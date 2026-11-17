# Octopus skills catalog

This catalog holds C# script skills for Octopus, The Model Brain for Revit. Each version includes its source, manifest, guidance and example inputs. Published versions are immutable. The owner reviews and merges contributions before GitHub Pages publishes and signs them.

In the Octopus client, open **My skills → Browse catalog**, choose a skill and install it. The catalog address and embedded review public key are supplied with the Octopus release. The client integration is phase 3; until it is available, the command-line tool can validate and pack local packages. A reviewed badge requires the client's verification of the package identity, content hash and signature. Running still follows the person's approval setting.

To contribute, see CONTRIBUTING.md. Contributors open pull requests with unpacked source under skills/<id>/<version>/. Review includes the source and its behavior in Revit. The signing secret is used only after merge.

Build the tool with .NET 8:

    dotnet build tools/OctopusSkill/OctopusSkill.csproj -c Release
    dotnet tools/OctopusSkill/bin/Release/net8.0/octoskill.dll validate skills/example.hello-octopus/1.0.0

Without Revit API assemblies, validation checks syntax and System-level policy. Full compilation and Autodesk policy require --revit-api and are repeated by Octopus before execution.

Publishing requires GitHub Pages configured for GitHub Actions and an owner-provided OCTOPUS_SIGNING_KEY secret containing a PKCS#8 P-256 private PEM. The owner manages the key. The public key is `keys/octopus-review-public.pem` and is also built into Octopus; anyone can check a signature with `octoskill verify <package> --signature <base64> --public-key keys/octopus-review-public.pem`. The publishing workflow uses ECDSA P-256 / SHA-256 and IEEE P1363 signatures.

License: MIT (see LICENSE). By opening a pull request you agree that your skill is published under the MIT license.
