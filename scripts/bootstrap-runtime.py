"""Optional, verified parallel download of the two large official self-contained runtime packs."""
import base64
import concurrent.futures
import hashlib
from pathlib import Path
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
folder = root / '.tools' / 'nuget-local'
folder.mkdir(parents=True, exist_ok=True)
packages = [
    ('microsoft.netcore.app.runtime.win-x64', 39979900, 'mLWGagTP5n7nMfJQI2XpXRYgvPcnyYvAc8ZiDfum8ShM0UcrPDyfW4Lqo45UtlCHCucERUHQuMTI6ybVweCN9A=='),
    ('microsoft.windowsdesktop.app.runtime.win-x64', 39195036, 'Hxj4llZzFIss9eBt/EKnxessXLi3/wj6Cwrqp2ZL1j74Pmg8VM+IZd9B7bWGt8flKre7z6cJwoRY5YpwJqX44Q=='),
]
chunk_size = 256 * 1024

def get_chunk(name, size, path, start):
    end = min(size, start + chunk_size) - 1
    url = f'https://api.nuget.org/v3-flatcontainer/{name}/10.0.14/{name}.10.0.14.nupkg'
    for attempt in range(5):
        try:
            request = urllib.request.Request(url + f'?range={start}&attempt={attempt}', headers={'Range': f'bytes={start}-{end}'})
            with urllib.request.urlopen(request, timeout=35) as response:
                if response.status != 206 or response.headers.get('Content-Range') != f'bytes {start}-{end}/{size}':
                    raise RuntimeError('Unexpected range response')
                data = response.read(end - start + 2)
                if len(data) != end - start + 1:
                    raise RuntimeError('Incomplete range')
            with path.open('r+b') as stream:
                stream.seek(start)
                stream.write(data)
            return
        except Exception:
            if attempt == 4:
                raise
            time.sleep(attempt + 1)

jobs = []
with concurrent.futures.ThreadPoolExecutor(max_workers=32) as pool:
    for name, size, checksum in packages:
        path = folder / f'{name}.10.0.14.nupkg.partial'
        if not path.exists() or path.stat().st_size != size:
            with path.open('wb') as stream:
                stream.truncate(size)
        with path.open('rb') as stream:
            for start in range(0, size, chunk_size):
                stream.seek(start)
                if stream.read(16) == bytes(16):
                    jobs.append(pool.submit(get_chunk, name, size, path, start))
    for index, job in enumerate(concurrent.futures.as_completed(jobs), 1):
        job.result()
        if index % 50 == 0:
            print(f'Runtime pack chunks: {index}/{len(jobs)}', flush=True)
for name, size, checksum in packages:
    path = folder / f'{name}.10.0.14.nupkg.partial'
    if base64.b64encode(hashlib.sha512(path.read_bytes()).digest()).decode() != checksum:
        raise RuntimeError(f'Official SHA512 mismatch for {name}')
    path.replace(folder / f'{name}.10.0.14.nupkg')
    print(f'Verified: {name}', flush=True)
