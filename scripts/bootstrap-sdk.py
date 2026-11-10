"""Download the pinned official SDK using bounded HTTP range requests; verify before extraction."""
import concurrent.futures
import hashlib
import pathlib
import urllib.request
import zipfile
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
VERSION = "10.0.403"
URL = f"https://dotnetcli.blob.core.windows.net/dotnet/Sdk/{VERSION}/dotnet-sdk-{VERSION}-win-x64.zip"
URLS = [URL.replace("dotnetcli.blob.core.windows.net", "dotnetcli.azureedge.net"), URL, URL.replace("dotnetcli.blob.core.windows.net", "builds.dotnet.microsoft.com")]
CHUNK = 256 * 1024
SIZE = 300745255
SHA512 = "7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48"
archive = ROOT / ".tools" / "sdk-verified.zip"
archive.parent.mkdir(exist_ok=True)
if not archive.exists() or archive.stat().st_size != SIZE:
    with archive.open("wb") as stream:
        stream.truncate(SIZE)

def missing(start):
    with archive.open("rb") as stream:
        stream.seek(start)
        return stream.read(16) == bytes(16)

def part(start):
    end = min(SIZE, start + CHUNK) - 1
    for attempt in range(4):
        try:
            request = urllib.request.Request(URLS[attempt % len(URLS)] + f"?range={start}&attempt={attempt}", headers={"Range": f"bytes={start}-{end}"})
            with urllib.request.urlopen(request, timeout=40) as response:
                if response.status != 206 or response.headers.get("Content-Range") != f"bytes {start}-{end}/{SIZE}":
                    raise RuntimeError("Range response required")
                content = response.read(end - start + 2)
                if len(content) != end - start + 1:
                    raise RuntimeError("Incomplete range")
            with archive.open("r+b") as stream:
                stream.seek(start)
                stream.write(content)
            return len(content)
        except Exception as error:
            print(f"Retry chunk {start}: {type(error).__name__}", flush=True)
            if attempt == 3:
                raise
            time.sleep(attempt + 1)

offsets = [n for n in range(0, SIZE, CHUNK) if missing(n)]
downloaded = SIZE - sum(min(CHUNK, SIZE - n) for n in offsets)
print(f"SDK remaining chunks: {len(offsets)}", flush=True)
with concurrent.futures.ThreadPoolExecutor(max_workers=24) as pool:
    jobs = [pool.submit(part, n) for n in offsets]
    for task in concurrent.futures.as_completed(jobs):
        downloaded += task.result()
        if downloaded // (40 * 1024 * 1024) != (downloaded - task.result()) // (40 * 1024 * 1024):
            print(f"SDK: {downloaded * 100 // SIZE}%", flush=True)
if hashlib.sha512(archive.read_bytes()).hexdigest() != SHA512:
    raise RuntimeError("Official SDK SHA512 mismatch")
with zipfile.ZipFile(archive) as bundle:
    bundle.extractall(ROOT / ".tools" / "dotnet")
print("SDK verified and extracted", flush=True)
