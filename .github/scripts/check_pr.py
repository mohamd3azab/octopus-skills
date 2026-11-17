"""Validate skill-only pull requests using the maintainer's CLI checkout."""
import io
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import zipfile


def main():
    base, head = os.environ["BASE_SHA"], os.environ["HEAD_SHA"]
    names = subprocess.check_output(
        ["git", "diff", "--no-renames", "--name-only", "-z", base, head]
    ).decode().split("\0")
    versions, problems = set(), []
    for name in filter(None, names):
        parts = pathlib.PurePosixPath(name).parts
        if not parts or parts[0] != "skills":
            problems.append(
                f"Disallowed pull request path: {name}. "
                "Tool and workflow changes come only from maintainers on main."
            )
            continue
        if len(parts) < 4:
            problems.append(f"Invalid skill version path: {name}")
            continue
        versions.add("/".join(parts[:3]))

    # Refuse infrastructure changes before reading or validating any PR contents.
    if problems:
        for problem in problems:
            print(problem, file=sys.stderr)
        return 1

    cli = pathlib.Path("tools/OctopusSkill/bin/Release/net8.0/octoskill.dll").resolve()
    for folder in sorted(versions):
        prior = subprocess.check_output(["git", "ls-tree", base, "--", folder]).decode().strip()
        if prior:
            problems.append(f"Published version is immutable: {folder}. Submit a new version.")
            continue
        # Inspect proposed skills separately; tools and this checker stay at the base revision.
        archive = subprocess.check_output(["git", "archive", "--format=zip", head, "--", folder])
        with tempfile.TemporaryDirectory(prefix="octopus-pr-") as temporary:
            with zipfile.ZipFile(io.BytesIO(archive)) as package:
                if any((entry.external_attr >> 16) & 0xF000 == 0xA000 for entry in package.infolist()):
                    problems.append(f"Symbolic links are not allowed: {folder}")
                    continue
                package.extractall(temporary)
            path = pathlib.Path(temporary) / folder
            try:
                manifest = json.loads((path / "skill.json").read_text(encoding="utf-8-sig"))
                if manifest.get("id") != path.parent.name or manifest.get("version") != path.name:
                    problems.append(f"Folder id/version does not match manifest: {folder}")
            except (OSError, ValueError, AttributeError) as ex:
                problems.append(f"{folder}: {ex}")
            result = subprocess.run(["dotnet", str(cli), "validate", str(path)])
            if result.returncode:
                problems.append(f"Skill validation failed: {folder}")
    for problem in problems:
        print(problem, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
