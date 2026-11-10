"""Verify architecture, self-contained dependencies, clean distribution, manifests and ZIP contents."""
from pathlib import Path
import hashlib
import json
import struct
import zipfile
import argparse

parser = argparse.ArgumentParser()
parser.add_argument('--create-package', action='store_true', help='Explicitly authorize creating a portable ZIP after verification')
options = parser.parse_args()

root = Path(__file__).resolve().parents[1]
package = root / 'dist' / '俄语课堂字幕-1.1'

def machine(path):
    with path.open('rb') as stream:
        stream.seek(0x3c)
        offset = struct.unpack('<I', stream.read(4))[0]
        stream.seek(offset)
        if stream.read(4) != b'PE\0\0':
            raise RuntimeError('Invalid executable')
        return struct.unpack('<H', stream.read(2))[0]

assert machine(package / 'ClassroomCaption.exe') == 0x8664
assert machine(package / 'ClassroomProbe.exe') == 0x14c
runtime = json.loads((package / 'ClassroomCaption.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']
assert runtime['tfm'] == 'net10.0'
assert any(f['name'] == 'Microsoft.WindowsDesktop.App' for f in runtime['includedFrameworks'])
for name in ['coreclr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll', 'NAudio.dll', 'ClassroomCaption.Core.dll', '使用说明.txt', '验证说明.txt']:
    assert (package / name).is_file(), name
for report in ['自动验证结果.json', '窗口组件验证结果.json']:
    value = json.loads((package / report).read_text(encoding='utf-8'))
    assert value['failed'] == 0

files = [p for p in package.rglob('*') if p.is_file()]
for path in files:
    assert path.suffix.lower() not in {'.vault', '.jsonl', '.wav', '.pcm', '.mp3', '.pdb'}, path.name
    assert 'data' not in path.relative_to(package).parts
    if path.suffix.lower() in {'.json', '.txt', '.config'}:
        content = path.read_text(encoding='utf-8-sig')
        assert 'synthetic-aliyun-secret-987' not in content and 'test-only-long-password-2026' not in content

manifest = {}
for line in (package / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
    checksum, relative = line.split('  ', 1)
    target = (package / relative).resolve()
    assert target.is_relative_to(package.resolve())
    assert hashlib.sha256(target.read_bytes()).hexdigest() == checksum, relative
    manifest[relative] = checksum
assert len(manifest) == len(files) - 1

if not options.create_package:
    print(json.dumps({'files': len(files), 'verificationOnly': True, 'portableZipCreated': False}))
    raise SystemExit(0)

archive = root / 'dist' / '俄语课堂字幕-1.1.0-win-x64.zip'
with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
    for path in files:
        bundle.write(path, arcname='俄语课堂字幕/' + path.relative_to(package).as_posix())
with zipfile.ZipFile(archive) as bundle:
    assert bundle.testzip() is None
    assert len(bundle.namelist()) == len(files)
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
(archive.parent / (archive.name + '.sha256')).write_text(checksum + '  ' + archive.name + '\n', encoding='utf-8')
result = {'files': len(files), 'unpackedBytes': sum(p.stat().st_size for p in files), 'zipBytes': archive.stat().st_size, 'zipSha256': checksum,
          'mainArchitecture': 'x64', 'probeArchitecture': 'x86', 'selfContained': True, 'containsCredentials': False, 'containsAudio': False}
(root / 'artifacts' / 'package-verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result, ensure_ascii=False))
