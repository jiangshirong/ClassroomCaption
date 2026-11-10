const http = require('http');
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');
const apk = path.join(__dirname, 'app/build/outputs/apk/debug/app-debug.apk');
const address = new URL(fs.readFileSync(path.join(root, 'artifacts/android/download-url.txt'), 'utf8').trim());
const server = http.createServer((request, response) => {
  if (request.method !== 'GET' || request.url !== address.pathname) { response.writeHead(404); response.end(); return; }
  fs.stat(apk, (error, stat) => {
    if (error) { response.writeHead(503); response.end(); return; }
    response.writeHead(200, { 'Content-Type': 'application/vnd.android.package-archive', 'Content-Length': stat.size, 'Content-Disposition': 'attachment; filename="classroom-caption-test.apk"', 'Cache-Control': 'no-store' });
    fs.createReadStream(apk).on('error', () => response.destroy()).pipe(response);
  });
});
server.listen(Number(address.port), '0.0.0.0');
