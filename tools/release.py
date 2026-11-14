#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
"""Own the desktop release build, isolated export, package audit and player archive."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile


class DesktopRelease:
    VERSION = "4.7.2"
    TARGETS = {
        "linux-x64": ("Linux x86_64", "linux", "x86_64", "Vagrant.x86_64"),
        "windows-x64": ("Windows x86_64", "windows", "x86_64", "Vagrant.exe"),
        "macos-universal": ("macOS Universal", "macos", "universal", "Vagrant.app"),
    }

    def __init__(self, args):
        self.args = args
        self.root = Path(__file__).resolve().parents[1]
        self.engine = self.root / "addons/wol_engine"
        self.cache = Path.home() / ".cache/vagrant-release-tools" / self.VERSION
        self.cache.mkdir(parents=True, exist_ok=True)
        self.preset, self.native_platform, self.arch, self.executable = self.TARGETS[args.target]

    def run(self, *command, cwd=None):
        print("+", " ".join(map(str, command)), flush=True)
        subprocess.run(list(map(str, command)), cwd=cwd or self.root, check=True)

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

    def prepare(self):
        host = platform.system()
        if self.native_platform == "macos" and host != "Darwin":
            raise RuntimeError("Build the universal native macOS runtime on a macOS runner.")
        if self.args.godot:
            editor = Path(self.args.godot).resolve()
        else:
            slug = {"Linux": "linux_x86_64", "Windows": "win64", "Darwin": "macos.universal"}[host]
            directory = self.cache / ("editor-" + slug)
            if not directory.exists():
                self.unzip(self.download(f"Godot_v{self.VERSION}-stable_mono_{slug}.zip"), directory)
            if host == "Darwin":
                editor = next(directory.rglob("Contents/MacOS/Godot"))
            elif host == "Windows":
                editor = next(directory.rglob("*mono_win64.exe"))
            else:
                editor = next(directory.rglob("*mono_linux.x86_64"))
            editor.chmod(editor.stat().st_mode | 0o111)
        if not self.args.skip_native:
            command = [self.args.scons_python, "-m", "SCons", "-C", self.engine,
                       "game_root=../..", f"platform={self.native_platform}", f"arch={self.arch}",
                       "target=template_release", "precision=single", f"godot_editor={editor}", "-j4"]
            if host != "Windows" and self.native_platform == "windows":
                command.append("use_mingw=yes")
            self.run(*command)
        api = self.engine / ".dependencies/godot-api"
        profile = json.loads((api / "qualified.json").read_text())
        editor = Path(profile["executable"])
        if not editor.is_file():
            raise RuntimeError("The qualified editor profile is stale; rebuild the engine.")
        if host == "Windows":
            templates = Path(os.environ["APPDATA"]) / "Godot/export_templates"
        elif host == "Darwin":
            templates = Path.home() / "Library/Application Support/Godot/export_templates"
        else:
            templates = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "godot/export_templates"
        installed = templates / f"{self.VERSION}.stable.mono"
        if not (installed / "version.txt").is_file():
            self.unzip(self.download(f"Godot_v{self.VERSION}-stable_mono_export_templates.tpz"), installed, True)
        return editor, api

    def audit(self, output):
        # Inspect loose files as well as the PCK, whose contents the exported player checks below.
        forbidden = {".cs", ".cpp", ".hpp", ".h", ".pdb", ".mdb", ".csproj", ".sln", ".cue", ".iso", ".chd", ".xml"}
        for file in output.rglob("*"):
            if file.is_symlink():
                raise RuntimeError("Player packages must not depend on checkout symlinks: " + str(file))
            if file.is_file() and file.suffix.lower() in forbidden:
                raise RuntimeError("Source, symbols or disc image in player: " + str(file))
            if file.is_file() and any(p in ("disc", "test-results", ".git", ".dependencies") for p in file.relative_to(output).parts):
                raise RuntimeError("Private/development data in player: " + str(file))
        expected = hashlib.sha256((self.engine / ".dependencies/godot-api/api/Release/GodotSharp.dll").read_bytes()).hexdigest()
        apis = list(output.rglob("GodotSharp.dll"))
        if not apis or any(hashlib.sha256(p.read_bytes()).hexdigest() != expected for p in apis):
            raise RuntimeError("The player did not retain the qualified Release GodotSharp API.")

    def build(self):
        editor, api = self.prepare()
        output = Path(self.args.output or self.root / "dist" / self.args.target).resolve()
        if output.exists() and any(output.iterdir()):
            raise RuntimeError("Choose an empty output directory; existing files are not removed.")
        output.mkdir(parents=True, exist_ok=True)
        version = self.args.version
        if not version or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-" for c in version):
            raise RuntimeError("Version must contain only letters, digits, dots, hyphens or underscores.")
        # A whitelist prevents local discs, caches, test assets and unrelated engine content entering the exporter.
        with tempfile.TemporaryDirectory(prefix="vagrant-export-", dir=self.cache) as temp:
            stage = Path(temp)
            for name in ("project.godot", "Vagrant.csproj", "Vagrant.sln", "global.json", "export_presets.cfg", "installation", "runtime", "scenes"):
                source = self.root / name
                if source.is_dir():
                    shutil.copytree(source, stage / name)
                else:
                    shutil.copy2(source, stage / name)
            addon = stage / "addons/wol_engine"
            shutil.copytree(self.engine / "runtime", addon / "runtime")
            shutil.copy2(self.engine / "GodotApi.props", addon / "GodotApi.props")
            (addon / "native/bin").mkdir(parents=True)
            shutil.copy2(self.engine / "native/spatial.gdextension", addon / "native/spatial.gdextension")
            for binary in (self.engine / "native/bin").iterdir():
                if binary.suffix in (".so", ".dll", ".dylib"):
                    shutil.copy2(binary, addon / "native/bin" / binary.name)
            for configuration in ("Debug", "Release"):
                destination = addon / ".dependencies/godot-api/api" / configuration
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
        self.audit(output)
        shutil.copy2(self.root / "LICENSE", output / "LICENSE")
        shutil.copy2(self.root / "distribution/README.txt", output / "README.txt")
        notices = output / "licenses"
        notices.mkdir()
        for source in (self.root / "distribution").glob("GODOT-*.txt"):
            shutil.copy2(source, notices / source.name)
        shutil.copy2(self.engine / "native/THIRD_PARTY_NOTICES.txt", notices / "WOL-THIRD-PARTY-NOTICES.txt")
        shutil.copy2(self.engine / "native/src/ps1_spu/LICENSE", notices / "PS1-SPU-LICENSE.txt")
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
            "game_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=self.root, text=True).strip(),
            "engine_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=self.engine, text=True).strip(),
            "godot": self.VERSION, "native_sha256": native,
            "game_worktree_dirty": bool(subprocess.check_output(["git", "status", "--porcelain", "--ignore-submodules=all"], cwd=self.root)),
            "engine_worktree_dirty": bool(subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=self.engine)),
            "disc_content_included": False, "engine_source_included": False,
        }
        (output / "BUILD.json").write_text(json.dumps(manifest, indent=2) + "\n")
        host = {"Linux": "linux-x64", "Windows": "windows-x64", "Darwin": "macos-universal"}[platform.system()]
        if host == self.args.target:
            executable = output / self.executable
            if platform.system() == "Darwin":
                executable = executable / "Contents/MacOS/Vagrant"
            executable.chmod(executable.stat().st_mode | 0o111)
            with tempfile.TemporaryDirectory(prefix="vagrant-no-disc-") as empty:
                self.run(executable, "--headless", "--", "--verify-package", "--install-root=" + empty, cwd=output)
        archive = output.parent / f"Vagrant-{version}-{self.args.target}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as packed:
            for file in sorted(output.rglob("*")):
                if file.is_file():
                    packed.write(file, Path("Vagrant") / file.relative_to(output))
        archive.with_suffix(".zip.sha256").write_text(hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name + "\n")
        print("PLAYER_ARCHIVE:", archive, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", choices=DesktopRelease.TARGETS)
    parser.add_argument("--version", default="development")
    parser.add_argument("--output")
    parser.add_argument("--godot", help="Original .NET editor; the engine qualifies its matching API before export")
    parser.add_argument("--scons-python", default=sys.executable)
    parser.add_argument("--skip-native", action="store_true", help="Use already-built native libraries and qualified API")
    parser.add_argument("--audit", type=Path, help="Audit an existing player directory without building")
    args = parser.parse_args()
    release = DesktopRelease(args)
    if args.audit:
        release.audit(args.audit.resolve())
        print("PACKAGE_FILES_OK")
    else:
        release.build()
