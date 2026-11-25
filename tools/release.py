#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
"""Own the desktop release build, isolated export, package audit and player archive."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import shutil
import sys
import subprocess
import tempfile
import urllib.request
import zipfile


class DesktopRelease:
    VERSION = "4.7.2"
    TARGETS = {
        "linux-x64": ("Linux x86_64", "linux", "x86_64", "ProjectVagrant.x86_64"),
        "windows-x64": ("Windows x86_64", "windows", "x86_64", "ProjectVagrant.exe"),
        "macos-universal": ("macOS Universal", "macos", "universal", "Project Vagrant.app"),
    }

    def __init__(self, args):
        self.args = args
        self.root = Path(__file__).resolve().parents[1]
        self.engine = self.root / "addons/wol_engine"
        self.cache = Path.home() / ".cache/vagrant-release-tools" / self.VERSION
        self.cache.mkdir(parents=True, exist_ok=True)
        self.preset, self.native_platform, self.arch, self.executable = self.TARGETS[args.target or "linux-x64"]

    def run(self, *command, cwd=None):
        print("+", " ".join(map(str, command)), flush=True)
        subprocess.run(list(map(str, command)), cwd=cwd or self.root, check=True)

    def preflight(self, *, managed=False, source=False):
        """Check build inputs before downloads, exports or output-directory creation."""
        errors = []
        host = platform.system()
        target = {"Linux": "linux-x64", "Windows": "windows-x64", "Darwin": "macos-universal"}.get(host)
        if self.args.target != target:
            errors.append(f"Build {self.args.target} on its native host (this host is {host}).")
        if sys.version_info < (3, 12):
            errors.append("Install Python 3.12 or newer.")
        if managed:
            requested = json.loads((self.root / "global.json").read_text())["sdk"]["version"]
            dotnet = shutil.which("dotnet")
            if not dotnet:
                errors.append(f"Install .NET SDK {requested} and put dotnet on PATH (required by global.json).")
            else:
                # Let the SDK resolver enforce global.json, including its roll-forward policy.
                result = subprocess.run([dotnet, "--version"], cwd=self.root, capture_output=True, text=True)
                if result.returncode:
                    installed = subprocess.run([dotnet, "--list-sdks"], cwd=self.root, capture_output=True, text=True)
                    versions = ", ".join(line.split()[0] for line in installed.stdout.splitlines() if line.strip()) or "none"
                    errors.append(f"Install .NET SDK {requested}, as required by global.json. Installed SDKs: {versions}.")
        identity = {"game_source": "archive", "game_commit": None, "game_worktree_dirty": None}
        if source and (self.root / ".git").exists():
            git = shutil.which("git")
            if not git:
                errors.append("Install Git and put it on PATH to record this checkout's revision, or build from a source ZIP.")
            else:
                revision = subprocess.run([git, "rev-parse", "--verify", "HEAD"], cwd=self.root, capture_output=True, text=True)
                status = subprocess.run([git, "status", "--porcelain", "--ignore-submodules=all"], cwd=self.root, capture_output=True, text=True)
                if revision.returncode or status.returncode:
                    errors.append("Cannot read this checkout's Git revision/status. Repair the checkout or use a source ZIP.")
                else:
                    identity = {"game_source": "git", "game_commit": revision.stdout.strip(), "game_worktree_dirty": bool(status.stdout)}
        if errors:
            raise RuntimeError("Build prerequisites are not ready:\n- " + "\n- ".join(errors))
        return identity

    def verify_projection(self):
        self.preflight(managed=True)
        editor, _ = self.prepare()
        # Editor import creates UID metadata beside scripts. Keep those writes out of the immutable SDK.
        with tempfile.TemporaryDirectory(prefix="vagrant-projection-", dir=self.cache) as temporary:
            stage = Path(temporary)
            for name in ("project.godot", "Vagrant.csproj", "Vagrant.sln", "global.json", "Directory.Build.props", "export_presets.cfg",
                         "runtime", "gameplay", "scenes", "tests"):
                source = self.root / name
                if source.is_dir():
                    shutil.copytree(source, stage / name, ignore=shutil.ignore_patterns("bin", "obj"))
                else:
                    shutil.copy2(source, stage / name)
            shutil.copytree(self.engine, stage / "addons/wol_engine")
            (stage / ".godot").mkdir()
            (stage / ".godot/extension_list.cfg").write_text("res://addons/wol_engine/native/spatial.gdextension\n")
            self.run("dotnet", "build", stage / "Vagrant.csproj", "--nologo")
            self.run(editor, "--headless", "--editor", "--import", "--path", stage, "--quit")
            for scene in ("actor_projection_contracts", "animation_script_contracts", "room_ordering_contracts"):
                self.run(editor, "--headless", "--path", stage, f"res://tests/{scene}.tscn",
                         "--", "--install-root=" + str(stage / "empty-installation"))

    def download(self, filename):
        base = f"https://github.com/godotengine/godot/releases/download/{self.VERSION}-stable/"
        sums = urllib.request.urlopen(base + "SHA512-SUMS.txt").read().decode()
        expected = next(line.split()[0] for line in sums.splitlines() if line.endswith(filename))
        target = self.cache / filename
        if not target.exists():
            partial = target.with_suffix(target.suffix + ".download")
            urllib.request.urlretrieve(base + filename, partial)
            partial.replace(target)
        with target.open("rb") as source:
            if hashlib.file_digest(source, "sha512").hexdigest() != expected:
                raise RuntimeError(f"Official checksum mismatch: {filename}")
        return target

    def unzip(self, archive, destination, strip_templates=False):
        with zipfile.ZipFile(archive) as packed:
            for entry in packed.infolist():
                path = Path(entry.filename)
                if path.is_absolute() or ".." in path.parts:
                    raise RuntimeError("Unsafe archive path: " + entry.filename)
                if entry.is_dir():
                    continue
                if strip_templates:
                    if path.parts[0] != "templates":
                        raise RuntimeError("Unexpected template archive entry")
                    path = Path(*path.parts[1:])
                output = destination / path
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_bytes(packed.read(entry))
                mode = entry.external_attr >> 16
                if mode:
                    output.chmod(mode & 0o777)

    def vendor(self):
        if self.engine.exists():
            raise RuntimeError("Move the existing addon aside before replacing the vendored release.")
        release = {"schema": 1, "godot": self.VERSION, "targets": {}}
        with tempfile.TemporaryDirectory(prefix="vagrant-vendor-", dir=self.cache) as temp:
            stage = Path(temp)
            for archive_name in self.args.vendor_runtime:
                archive = Path(archive_name).resolve()
                digest = hashlib.sha256(archive.read_bytes()).hexdigest()
                if archive.with_suffix(".zip.sha256").read_text().split()[0] != digest:
                    raise RuntimeError("Engine release archive checksum mismatch")
                with zipfile.ZipFile(archive) as packed:
                    manifest = json.loads(packed.read("RUNTIME.json"))
                    commit, target = manifest["commit"], manifest["target"]
                    if target not in self.TARGETS or target in release["targets"] or manifest["godot"] != self.VERSION:
                        raise RuntimeError("Each matching desktop SDK must be provided exactly once")
                    if release.setdefault("commit", commit) != commit:
                        raise RuntimeError("Engine SDK commits do not match")
                    source_release = manifest.get("release", "https://github.com/RakuenSoftware/wol-engine/releases/tag/runtime-" + commit)
                    allowed_releases = {
                        "https://github.com/RakuenSoftware/" + repository + "/releases/tag/" + prefix + commit
                        for repository, prefixes in (("wol-engine", ("runtime-", "resources-")), ("wol-engine-release", ("runtime-",)))
                        for prefix in prefixes
                    }
                    if source_release not in allowed_releases:
                        raise RuntimeError("Unexpected engine release provenance")
                    if release.setdefault("release", source_release) != source_release:
                        raise RuntimeError("Engine SDK releases do not match")
                    if release.setdefault("binary_commit", manifest.get("binary_commit", commit)) != manifest.get("binary_commit", commit):
                        raise RuntimeError("Engine SDK binary revisions do not match")
                    expected = set(manifest["files"]) | {"RUNTIME.json"}
                    if {p.filename for p in packed.infolist() if not p.is_dir()} != expected:
                        raise RuntimeError("Engine release archive file list mismatch")
                    for name, checksum in manifest["files"].items():
                        path = Path(name)
                        if path.is_absolute() or ".." in path.parts:
                            raise RuntimeError("Unsafe engine release path")
                        data = packed.read(name)
                        if hashlib.sha256(data).hexdigest() != checksum:
                            raise RuntimeError("Engine release file checksum mismatch: " + name)
                        if name.startswith("editor/"):
                            continue
                        if path.suffix.lower() in (".c", ".cpp", ".cc", ".cxx", ".hpp", ".h", ".hxx", ".pdb", ".snupkg", ".csproj", ".sln", ".gd", ".gdshader", ".gdshaderinc", ".glsl", ".hlsl", ".shader", ".py", ".sh", ".ps1", ".js", ".ts", ".rs", ".map", ".log"):
                            raise RuntimeError("Implementation/development file in engine SDK: " + name)
                        if path.suffix == ".cs":
                            if data.strip():
                                raise RuntimeError("Engine implementation source is not vendored")
                            data = b"\n"
                        if path.suffix in (".txt", ".md", ".svg", ".uid", ".tscn", ".gdextension", ".props", ".json"):
                            data = data.replace(b"\r\n", b"\n")
                        if name.startswith(("managed/", "portable/", ".dependencies/")) or name in ("GodotApi.props", "WolEngine.props"):
                            destination = stage / "platforms" / target / path
                        else:
                            destination = stage / path
                        if destination.exists() and destination.read_bytes() != data:
                            raise RuntimeError("Common SDK resources differ: " + name)
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        destination.write_bytes(data)
                    (stage / "platforms" / target / "RUNTIME.json").write_bytes(packed.read("RUNTIME.json").replace(b"\r\n", b"\n"))
                    release["targets"][target] = {"asset": archive.name, "sha256": digest}
            if set(release["targets"]) != set(self.TARGETS):
                raise RuntimeError("All three desktop runtime SDKs are required")
            release["files"] = {p.relative_to(stage).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                                for p in sorted(stage.rglob("*")) if p.is_file()}
            (stage / "VENDOR.json").write_text(json.dumps(release, indent=2) + "\n")
            shutil.copytree(stage, self.engine)
        print("VENDORED_ENGINE:", release["release"])

    def prepare(self):
        host = platform.system()
        self.runtime = json.loads((self.engine / "VENDOR.json").read_text())
        if self.runtime["godot"] != self.VERSION or self.args.target not in self.runtime["targets"]:
            raise RuntimeError("Vendored engine Godot version or target mismatch")
        expected = set(self.runtime["files"]) | {"VENDOR.json"}
        actual = {p.relative_to(self.engine).as_posix() for p in self.engine.rglob("*") if p.is_file()}
        if actual != expected:
            raise RuntimeError("Vendored runtime file list does not match VENDOR.json")
        for name, checksum in self.runtime["files"].items():
            if hashlib.sha256((self.engine / name).read_bytes()).hexdigest() != checksum:
                raise RuntimeError("Vendored runtime checksum mismatch: " + name)
        self.sdk = self.engine / "platforms" / self.args.target
        api = self.sdk / ".dependencies/godot-api"
        slug = {"Linux": "linux_x86_64", "Windows": "win64", "Darwin": "macos.universal"}[host]
        original = self.cache / ("official-editor-" + slug)
        if not original.exists():
            self.unzip(self.download(f"Godot_v{self.VERSION}-stable_mono_{slug}.zip"), original)
        pattern = {"Linux": "*mono_linux.x86_64", "Windows": "*mono_win64.exe", "Darwin": "Contents/MacOS/Godot"}[host]
        source_editor = next(original.rglob(pattern))
        qualified = self.cache / "qualified" / self.runtime["commit"] / self.args.target
        if host == "Darwin":
            bundle = source_editor.parents[2]
            destination = qualified / "Godot.app"
        else:
            bundle = source_editor.parent
            destination = qualified / "editor"
        if not destination.exists():
            shutil.copytree(bundle, destination)
        editor = destination / source_editor.relative_to(bundle)
        installed_api = destination / ("Contents/Resources/GodotSharp/Api" if host == "Darwin" else "GodotSharp/Api")
        for configuration in ("Debug", "Release"):
            for library in (api / "api" / configuration).glob("*.dll"):
                shutil.copy2(library, installed_api / configuration / library.name)
        if host == "Darwin":
            # Retain the official editor's own entitlements when re-signing its replaced API resources.
            entitlements = subprocess.check_output(["codesign", "-d", "--entitlements", ":-", str(bundle)])
            signing = qualified / "editor.entitlements"
            signing.write_bytes(entitlements)
            self.run("codesign", "--force", "--deep", "--sign", "-", "--options=runtime", "--entitlements", signing, destination)
            self.run("codesign", "--verify", "--deep", "--strict", destination)
        editor.chmod(editor.stat().st_mode | 0o111)
        if host == "Windows":
            templates = Path(os.environ["APPDATA"]) / "Godot/export_templates"
        elif host == "Darwin":
            templates = Path.home() / "Library/Application Support/Godot/export_templates"
        else:
            templates = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "godot/export_templates"
        installed = templates / f"{self.VERSION}.stable.mono"
        if not (installed / "version.txt").is_file():
            self.unzip(self.download(f"Godot_v{self.VERSION}-stable_mono_export_templates.tpz"), installed, True)
        (self.root / ".godot").mkdir(exist_ok=True)
        (self.root / ".godot/extension_list.cfg").write_text("res://addons/wol_engine/native/spatial.gdextension\n")
        return editor, api

    def audit(self, output):
        # Inspect loose files as well as the PCK, whose contents the exported player checks below.
        retail = json.loads((self.root / "gameplay/installation/retail_digests.json").read_text())
        retail_hashes = set(retail.values())
        retail_formats = {Path(name).suffix.lower() for name in retail} | {".str", ".xa", ".cue", ".iso", ".chd", ".img"}
        forbidden = {".cs", ".cpp", ".hpp", ".h", ".pdb", ".mdb", ".csproj", ".sln", ".cue", ".iso", ".chd", ".xml"}
        for file in output.rglob("*"):
            if file.is_symlink():
                raise RuntimeError("Player packages must not depend on checkout symlinks: " + str(file))
            if file.is_file() and file.suffix.lower() in forbidden:
                raise RuntimeError("Source, symbols or disc image in player: " + str(file))
            if file.is_file() and file.suffix.lower() in retail_formats:
                raise RuntimeError("Retail disc file format in player: " + str(file))
            if file.is_file() and any(p in ("disc", "cache", "captures", "screenshots", "test-results", ".git", ".dependencies") for p in file.relative_to(output).parts):
                raise RuntimeError("Private/development data in player: " + str(file))
            if file.is_file():
                with file.open("rb") as source:
                    signature = source.read(12)
                    source.seek(0)
                    digest = hashlib.file_digest(source, "sha256").hexdigest()
                if (file.stat().st_size >= 32 and digest in retail_hashes) or signature.startswith(b"PS-X EXE") or signature == b"\0" + b"\xff" * 10 + b"\0":
                    raise RuntimeError("Retail disc content/signature in player: " + str(file))
        for name, source in (("GodotSharp.dll", ".dependencies/godot-api/api/Release/GodotSharp.dll"),
                             ("WolEngine.dll", "managed/Release/WolEngine.dll")):
            expected = hashlib.sha256((self.sdk / source).read_bytes()).hexdigest()
            libraries = list(output.rglob(name))
            if not libraries or any(hashlib.sha256(p.read_bytes()).hexdigest() != expected for p in libraries):
                raise RuntimeError("The player did not retain the pinned Release library: " + name)

        expected_core = hashlib.sha256((self.sdk / "portable/Release/WolEngine.Core.dll").read_bytes()).hexdigest()
        libraries = list(output.rglob("WolEngine.Core.dll"))
        if not libraries or any(hashlib.sha256(p.read_bytes()).hexdigest() != expected_core for p in libraries):
            raise RuntimeError("The player did not retain the pinned portable core library")

    def build(self):
        output = Path(self.args.output or self.root / "dist" / self.args.target).resolve()
        if output.exists() and any(output.iterdir()):
            raise RuntimeError("Choose an empty output directory; existing files are not removed.")
        version = self.args.version
        if not version or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-" for c in version):
            raise RuntimeError("Version must contain only letters, digits, dots, hyphens or underscores.")
        identity = self.preflight(managed=True, source=True)
        editor, api = self.prepare()
        output.mkdir(parents=True, exist_ok=True)
        # A whitelist prevents local discs, caches, test assets and unrelated engine content entering the exporter.
        with tempfile.TemporaryDirectory(prefix="vagrant-export-", dir=self.cache) as temp:
            stage = Path(temp)
            for name in ("project.godot", "Vagrant.csproj", "Vagrant.sln", "global.json", "Directory.Build.props", "export_presets.cfg", "runtime", "gameplay", "scenes"):
                source = self.root / name
                if source.is_dir():
                    shutil.copytree(source, stage / name, ignore=shutil.ignore_patterns("bin", "obj"))
                else:
                    shutil.copy2(source, stage / name)
            addon = stage / "addons/wol_engine"
            shutil.copytree(self.engine / "runtime", addon / "runtime")
            sdk = addon / "platforms" / self.args.target
            sdk.mkdir(parents=True)
            shutil.copy2(self.sdk / "GodotApi.props", sdk / "GodotApi.props")
            shutil.copy2(self.sdk / "WolEngine.props", sdk / "WolEngine.props")
            shutil.copytree(self.sdk / "managed", sdk / "managed")
            shutil.copytree(self.sdk / "portable", sdk / "portable")
            (addon / "native/bin").mkdir(parents=True)
            shutil.copy2(self.engine / "native/spatial.gdextension", addon / "native/spatial.gdextension")
            suffix = {"linux": "so", "windows": "dll", "macos": "dylib"}[self.native_platform]
            binary = self.engine / f"native/bin/libspatial.{self.native_platform}.template_release.{self.arch}.{suffix}"
            shutil.copy2(binary, addon / "native/bin" / binary.name)
            for configuration in ("Debug", "Release"):
                destination = sdk / ".dependencies/godot-api/api" / configuration
                destination.mkdir(parents=True)
                for dll in (api / "api" / configuration).glob("*.dll"):
                    shutil.copy2(dll, destination / dll.name)
            (stage / ".godot").mkdir()
            (stage / ".godot/extension_list.cfg").write_text("res://addons/wol_engine/native/spatial.gdextension\n")
            preset = stage / "export_presets.cfg"
            text = preset.read_text()
            numeric = version.removeprefix("v")
            if numeric and all(c in "0123456789." for c in numeric):
                text = text.replace('application/short_version="0.1.0"', f'application/short_version="{numeric}"')
                text = text.replace('application/version="0.1.0"', f'application/version="{numeric}"')
            preset.write_text(text)
            # The editor loads Debug autoloads during import; the player is published as ExportRelease.
            self.run("dotnet", "build", stage / "Vagrant.csproj", "-c", "Debug", "--nologo")
            self.run("dotnet", "build", stage / "Vagrant.csproj", "-c", "ExportRelease", "--nologo")
            self.run(editor, "--headless", "--editor", "--import", "--path", stage, "--quit")
            self.run(editor, "--headless", "--path", stage, "--export-release", self.preset, output / self.executable)
        if self.native_platform == "macos":
            self.run("codesign", "--force", "--deep", "--sign", "-", "--preserve-metadata=entitlements", output / self.executable)
            self.run("codesign", "--verify", "--deep", "--strict", output / self.executable)
        self.audit(output)
        shutil.copy2(self.root / "LICENSE", output / "LICENSE")
        shutil.copy2(self.root / "distribution/README.txt", output / "README.txt")
        notices = output / "licenses"
        notices.mkdir()
        for source in (self.root / "distribution").glob("GODOT-*.txt"):
            shutil.copy2(source, notices / source.name)
        shutil.copy2(self.engine / "licenses/WOL-THIRD-PARTY-NOTICES.txt", notices / "WOL-THIRD-PARTY-NOTICES.txt")
        shutil.copy2(self.engine / "licenses/PS1-SPU-LICENSE.txt", notices / "PS1-SPU-LICENSE.txt")
        # The self-contained .NET runtime is distributed under its own MIT/third-party notices.
        nuget = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
        runtime_notices = 0
        for deps in output.rglob("Vagrant.deps.json"):
            for dependency in json.loads(deps.read_text()).get("libraries", {}):
                name, _, number = dependency.partition("/")
                if "Microsoft.NETCore.App.Runtime." not in name:
                    continue
                package = nuget / name.removeprefix("runtimepack.").lower() / number
                for filename in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
                    source = package / filename
                    if not source.is_file():
                        raise RuntimeError("Missing .NET runtime notice: " + str(source))
                    shutil.copy2(source, notices / f"DOTNET-{name}-{filename}")
                    runtime_notices += 1
        if runtime_notices == 0:
            raise RuntimeError("The self-contained .NET runtime/notices could not be identified.")
        native = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in output.rglob("*spatial*") if p.is_file()}
        manifest = {
            "version": version, "target": self.args.target,
            **identity,
            "engine_commit": self.runtime["commit"],
            "engine_binary_commit": self.runtime.get("binary_commit", self.runtime["commit"]),
            "godot": self.VERSION, "native_sha256": native,
            "engine_package_sha256": self.runtime["targets"][self.args.target]["sha256"],
            "disc_content_included": False, "engine_source_included": False,
        }
        (output / "BUILD.json").write_text(json.dumps(manifest, indent=2) + "\n")
        host = {"Linux": "linux-x64", "Windows": "windows-x64", "Darwin": "macos-universal"}[platform.system()]
        if host == self.args.target:
            executable = output / self.executable
            if platform.system() == "Darwin":
                with (executable / "Contents/Info.plist").open("rb") as info:
                    executable = executable / "Contents/MacOS" / plistlib.load(info)["CFBundleExecutable"]
            executable.chmod(executable.stat().st_mode | 0o111)
            with tempfile.TemporaryDirectory(prefix="vagrant-no-disc-") as empty:
                self.run(executable, "--headless", "--", "--verify-package", "--install-root=" + empty, cwd=output)
        archive = output.parent / f"ProjectVagrant-{version}-{self.args.target}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as packed:
            for file in sorted(output.rglob("*")):
                if file.is_file():
                    packed.write(file, Path("ProjectVagrant") / file.relative_to(output))
        archive.with_suffix(".zip.sha256").write_text(
            hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name + "\n",
            encoding="utf-8", newline="\n")
        print("PLAYER_ARCHIVE:", archive, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", choices=DesktopRelease.TARGETS, nargs="?")
    parser.add_argument("--version", default="development")
    parser.add_argument("--output")
    parser.add_argument("--vendor-runtime", nargs=3, metavar="SDK_ZIP", help="Vendor three upstream release SDKs; this maintainer operation is never run by CI")
    parser.add_argument("--verify-projection", action="store_true", help="Run synthetic attached-rig projection checks without game data")
    parser.add_argument("--prepare", action="store_true", help="Prepare the public editor/templates against vendored API binaries for development")
    parser.add_argument("--audit", type=Path, help="Audit an existing player directory without building")
    args = parser.parse_args()
    release = DesktopRelease(args)
    try:
        if args.vendor_runtime:
            release.vendor()
        elif not args.target:
            parser.error("a desktop target is required")
        elif args.verify_projection:
            release.verify_projection()
        elif args.prepare:
            editor, _ = release.prepare()
            print("QUALIFIED_EDITOR:", editor)
        elif args.audit:
            release.prepare()
            release.audit(args.audit.resolve())
            print("PACKAGE_FILES_OK")
        else:
            release.build()
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, "BUILD_FAILED: " + str(error) + "\n")
