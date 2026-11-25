# SPDX-License-Identifier: AGPL-3.0-only
"""Release preconditions and provenance; never export a player."""
import importlib.util
import hashlib
import zipfile
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('vagrant_release', Path(__file__).resolve().parents[2] / 'tools/release.py')
release_module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release_module)


class ReleasePreflightTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / 'global.json').write_text(json.dumps({'sdk': {'version': '10.0.403', 'rollForward': 'latestPatch'}}))
        self.release = release_module.DesktopRelease.__new__(release_module.DesktopRelease)
        self.release.root = self.root
        self.release.cache = self.root / 'cache'
        self.release.args = SimpleNamespace(target='windows-x64', version='development', output=str(self.root / 'player'))

    def tool(self, name):
        return '/tools/' + name

    def resolver(self, command, **kwargs):
        self.assertEqual(Path(kwargs['cwd']), self.root)
        self.assertEqual(command[1:], ['--version'])
        return subprocess.CompletedProcess(command, 0, '10.0.403\n', '')

    def test_reports_all_missing_windows_requirements_before_prepare_or_output(self):
        with patch.object(release_module.platform, 'system', return_value='Windows'), \
             patch.object(release_module.shutil, 'which', return_value=None), \
             patch.object(self.release, 'prepare') as prepare:
            with self.assertRaises(RuntimeError) as failure:
                self.release.build()
        message = str(failure.exception)
        for expected in ('10.0.403', 'dotnet'):
            self.assertIn(expected, message)
        prepare.assert_not_called()
        self.assertFalse(Path(self.release.args.output).exists())

    def test_sdk_resolver_enforces_global_json_and_reports_installed_versions(self):
        def incompatible(command, **kwargs):
            self.assertEqual(Path(kwargs['cwd']), self.root)
            return subprocess.CompletedProcess(command, 1 if command[-1] == '--version' else 0,
                                               '' if command[-1] == '--version' else '8.0.421 [SDKs]\n10.0.301 [SDKs]\n', '')
        with patch.object(release_module.platform, 'system', return_value='Windows'), \
             patch.object(release_module.shutil, 'which', side_effect=self.tool), \
             patch.object(release_module.subprocess, 'run', side_effect=incompatible):
            with self.assertRaisesRegex(RuntimeError, r'10\.0\.403.*8\.0\.421, 10\.0\.301'):
                self.release.preflight(managed=True)

    def test_zip_sources_do_not_require_git_and_record_unknown_provenance(self):
        with patch.object(release_module.platform, 'system', return_value='Windows'), \
             patch.object(release_module.shutil, 'which', return_value=None) as which, \
             patch.object(release_module.subprocess, 'run') as run:
            identity = self.release.preflight(source=True)
        self.assertEqual(identity, {'game_source': 'archive', 'game_commit': None, 'game_worktree_dirty': None})
        which.assert_not_called()
        run.assert_not_called()

    def test_git_checkouts_require_readable_metadata_up_front(self):
        (self.root / '.git').mkdir()
        with patch.object(release_module.platform, 'system', return_value='Windows'), \
             patch.object(release_module.shutil, 'which', return_value=None):
            with self.assertRaisesRegex(RuntimeError, 'Install Git'):
                self.release.preflight(source=True)
        with patch.object(release_module.platform, 'system', return_value='Windows'), \
             patch.object(release_module.shutil, 'which', side_effect=self.tool), \
             patch.object(release_module.subprocess, 'run', return_value=subprocess.CompletedProcess([], 128, '', 'bad metadata')):
            with self.assertRaisesRegex(RuntimeError, 'Repair the checkout'):
                self.release.preflight(source=True)

    def test_real_checkout_revision_and_dirty_state(self):
        git = shutil.which('git')
        if not git:
            self.skipTest('Git unavailable; source-ZIP mode is tested independently')
        subprocess.run([git, 'init', '-q', str(self.root)], check=True)
        subprocess.run([git, '-C', str(self.root), 'add', 'global.json'], check=True)
        subprocess.run([git, '-C', str(self.root), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                        '-c', 'commit.gpgsign=false', 'commit', '-qm', 'Deliberate input'], check=True)
        expected = subprocess.check_output([git, '-C', str(self.root), 'rev-parse', 'HEAD'], text=True).strip()
        with patch.object(release_module.platform, 'system', return_value='Windows'):
            clean = self.release.preflight(source=True)
            (self.root / 'local-change').write_text('deliberate input')
            dirty = self.release.preflight(source=True)
        self.assertEqual(clean, {'game_source': 'git', 'game_commit': expected, 'game_worktree_dirty': False})
        self.assertEqual(dirty, {**clean, 'game_worktree_dirty': True})

    def test_wrong_host_and_nonempty_output_fail_before_export(self):
        with patch.object(release_module.platform, 'system', return_value='Linux'):
            with self.assertRaisesRegex(RuntimeError, 'native host'):
                self.release.preflight()
        output = Path(self.release.args.output)
        output.mkdir()
        (output / 'keep').write_text('existing output')
        with patch.object(self.release, 'prepare') as prepare:
            with self.assertRaisesRegex(RuntimeError, 'empty output directory'):
                self.release.build()
        prepare.assert_not_called()
        self.assertEqual((output / 'keep').read_text(), 'existing output')

    def test_fresh_checkout_stages_gameplay_installation_for_checks_and_export(self):
        # Git does not preserve the old, now-empty installation directory.
        for name in ('project.godot', 'Vagrant.csproj', 'Vagrant.sln', 'Directory.Build.props', 'export_presets.cfg'):
            (self.root / name).write_text('deliberate project fixture')
        for name in ('runtime', 'gameplay/installation', 'scenes', 'tests'):
            (self.root / name).mkdir(parents=True)
        catalog = self.root / 'gameplay/installation/retail_digests.json'
        catalog.write_text('{"synthetic": "catalog fixture"}')
        self.release.cache.mkdir()
        self.release.engine = self.root / 'addons/wol_engine'
        self.release.sdk = self.release.engine / 'platforms/windows-x64'
        self.release.native_platform, self.release.arch = 'windows', 'x86_64'
        for name in ('runtime', 'native/bin', 'platforms/windows-x64/managed', 'platforms/windows-x64/portable'):
            (self.release.engine / name).mkdir(parents=True)
        for name in ('GodotApi.props', 'WolEngine.props'):
            (self.release.sdk / name).write_text('<Project/>')
        (self.release.engine / 'native/spatial.gdextension').write_text('deliberate extension fixture')
        (self.release.engine / 'native/bin/libspatial.windows.template_release.x86_64.dll').write_bytes(b'deliberate native fixture')
        api = self.root / 'api'
        for configuration in ('Debug', 'Release'):
            folder = api / 'api' / configuration
            folder.mkdir(parents=True)
            (folder / 'GodotSharp.dll').write_bytes(b'deliberate API fixture')
        self.assertFalse((self.root / 'installation').exists())

        for operation in ('verify_projection', 'build'):
            with self.subTest(operation=operation):
                def inspect_stage(*command, **kwargs):
                    self.assertEqual(command[:2], ('dotnet', 'build'))
                    stage = Path(command[2]).parent
                    self.assertFalse((stage / 'installation').exists())
                    self.assertEqual((stage / 'gameplay/installation/retail_digests.json').read_bytes(), catalog.read_bytes())
                    self.assertTrue((stage / 'addons/wol_engine/runtime').is_dir())
                    raise RuntimeError('Compilation stage observed')
                with patch.object(self.release, 'preflight', return_value={}), \
                     patch.object(self.release, 'prepare', return_value=('editor', api)), \
                     patch.object(self.release, 'run', side_effect=inspect_stage):
                    with self.assertRaisesRegex(RuntimeError, 'Compilation stage observed'):
                        getattr(self.release, operation)()

    def test_cli_missing_tools_is_actionable_without_traceback_or_partial_output(self):
        tools = self.root / 'tools'
        tools.mkdir()
        shutil.copy2(Path(release_module.__file__), tools / 'release.py')
        target = {'Linux': 'linux-x64', 'Windows': 'windows-x64', 'Darwin': 'macos-universal'}[release_module.platform.system()]
        output = self.root / 'output'
        result = subprocess.run([sys.executable, str(tools / 'release.py'), target, '--output', str(output)],
                                env={**os.environ, 'PATH': ''}, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('BUILD_FAILED: Build prerequisites are not ready', result.stderr)
        self.assertIn('10.0.403', result.stderr)
        self.assertNotIn('Traceback', result.stderr)
        self.assertFalse(output.exists())
        self.assertFalse((self.root / '.godot').exists())


class VendorPlatformCoreTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.release = release_module.DesktopRelease.__new__(release_module.DesktopRelease)
        self.release.engine = self.root / 'addon'
        self.release.cache = self.root

    def sdk(self, target, *, shared=b'shared resource', source=False):
        commit = 'a' * 40
        files = {'portable/Release/WolEngine.Core.dll': b'MZ deliberate fixture ' + target.encode(),
                 'portable/WolEngine.Core.props': b'<Project/>', 'runtime/branding/wol.svg': shared}
        if source:
            files['portable/Private.cs'] = b'namespace PrivateImplementation;'
        manifest = {'target': target, 'commit': commit, 'godot': '4.7.2',
                    'release': f'https://github.com/RakuenSoftware/wol-engine-release/releases/tag/runtime-{commit}',
                    'files': {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
        archive = self.root / (target + '.zip')
        with zipfile.ZipFile(archive, 'w') as packed:
            for name, data in files.items():
                packed.writestr(name, data)
            packed.writestr('RUNTIME.json', json.dumps(manifest))
        archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(archive.read_bytes()).hexdigest() + '  ' + archive.name + '\n')
        return archive

    def test_distinct_platform_core_bytes_are_preserved_and_hashed(self):
        self.release.args = SimpleNamespace(vendor_runtime=[self.sdk(target) for target in release_module.DesktopRelease.TARGETS])
        self.release.vendor()
        record = json.loads((self.release.engine / 'VENDOR.json').read_text())
        for target in release_module.DesktopRelease.TARGETS:
            name = f'platforms/{target}/portable/Release/WolEngine.Core.dll'
            actual = (self.release.engine / name).read_bytes()
            self.assertEqual(actual, b'MZ deliberate fixture ' + target.encode())
            self.assertEqual(record['files'][name], hashlib.sha256(actual).hexdigest())
        self.assertFalse((self.release.engine / 'portable').exists())

    def test_common_resources_still_require_matching_bytes(self):
        self.release.args = SimpleNamespace(vendor_runtime=[self.sdk('linux-x64'), self.sdk('windows-x64', shared=b'different'), self.sdk('macos-universal')])
        with self.assertRaisesRegex(RuntimeError, 'Common SDK resources differ'):
            self.release.vendor()
        self.assertFalse(self.release.engine.exists())

    def test_portable_paths_do_not_allow_private_source(self):
        self.release.args = SimpleNamespace(vendor_runtime=[self.sdk('linux-x64', source=True), self.sdk('windows-x64'), self.sdk('macos-universal')])
        with self.assertRaisesRegex(RuntimeError, 'implementation source'):
            self.release.vendor()
        self.assertFalse(self.release.engine.exists())


if __name__ == '__main__':
    unittest.main()
