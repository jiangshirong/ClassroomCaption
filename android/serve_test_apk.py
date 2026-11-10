from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import os
import secrets
import socket
import sys
import qrcode

apk = Path(__file__).resolve().parent / 'app/build/outputs/apk/debug/app-debug.apk'
artifact_dir = Path(__file__).resolve().parent.parent / 'artifacts/android'
artifact_dir.mkdir(parents=True, exist_ok=True)
token = secrets.token_urlsafe(18)
host = sys.argv[1]
port = 18768
url = f'http://{host}:{port}/{token}/classroom-caption.apk'

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != f'/{token}/classroom-caption.apk':
            self.send_error(404)
            return
        size = apk.stat().st_size
        self.send_response(200)
        self.send_header('Content-Type', 'application/vnd.android.package-archive')
        self.send_header('Content-Disposition', 'attachment; filename="classroom-caption-test.apk"')
        self.send_header('Content-Length', str(size))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        with apk.open('rb') as source:
            while chunk := source.read(1024 * 128):
                self.wfile.write(chunk)
    def log_message(self, fmt, *args):
        pass

server = ThreadingHTTPServer(('0.0.0.0', port), Handler)
qrcode.make(url).save(artifact_dir / 'download-qr.png')
(artifact_dir / 'download-url.txt').write_text(url, encoding='utf-8')
server.serve_forever()
