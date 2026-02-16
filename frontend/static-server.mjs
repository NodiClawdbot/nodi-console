import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const PORT = Number(process.env.PORT || 4200);
const DIST_DIR = process.env.DIST_DIR || path.join(__dirname, 'dist');
const BASE_PATH = (process.env.BASE_PATH || '/').trim() || '/';
const normalizedBase = BASE_PATH === '/' ? '/' : `/${BASE_PATH.replace(/^\/+|\/+$/g, '')}`;

function contentType(p) {
  const ext = path.extname(p).toLowerCase();
  if (ext === '.html') return 'text/html; charset=utf-8';
  if (ext === '.js') return 'application/javascript; charset=utf-8';
  if (ext === '.css') return 'text/css; charset=utf-8';
  if (ext === '.json') return 'application/json; charset=utf-8';
  if (ext === '.svg') return 'image/svg+xml';
  if (ext === '.png') return 'image/png';
  if (ext === '.jpg' || ext === '.jpeg') return 'image/jpeg';
  if (ext === '.ico') return 'image/x-icon';
  if (ext === '.txt') return 'text/plain; charset=utf-8';
  return 'application/octet-stream';
}

function sendFile(res, filePath) {
  try {
    const st = fs.statSync(filePath);
    if (!st.isFile()) return false;
    res.statusCode = 200;
    res.setHeader('Content-Type', contentType(filePath));
    res.setHeader('Cache-Control', 'no-cache');
    fs.createReadStream(filePath).pipe(res);
    return true;
  } catch {
    return false;
  }
}

const server = http.createServer((req, res) => {
  try {
    const url = new URL(req.url || '/', 'http://localhost');
    const pathname = decodeURIComponent(url.pathname);

    // Serve static files from DIST_DIR.
    // The Angular build emits asset URLs under BASE_PATH (e.g. /nodi/main.js).
    // When accessed directly (http://host:4200/nodi/...) we must strip BASE_PATH.
    // When behind a proxy that strips the prefix, pathname is already '/...'; this remains safe.

    const stripped =
      normalizedBase !== '/' && (pathname === normalizedBase || pathname.startsWith(normalizedBase + '/'))
        ? pathname.slice(normalizedBase.length) || '/'
        : pathname;

    const rel = stripped.replace(/^\//, '');
    const filePath = path.join(DIST_DIR, rel);

    // Direct file
    if (sendFile(res, filePath)) return;

    // Directory index
    if (sendFile(res, path.join(filePath, 'index.html'))) return;

    // SPA fallback
    if (sendFile(res, path.join(DIST_DIR, 'index.html'))) return;

    res.statusCode = 404;
    res.setHeader('Content-Type', 'text/plain; charset=utf-8');
    res.end('Not found');
  } catch {
    res.statusCode = 500;
    res.setHeader('Content-Type', 'text/plain; charset=utf-8');
    res.end('Server error');
  }
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`[nodi-clawdbot-frontend] serving ${DIST_DIR} on http://0.0.0.0:${PORT}/`);
});
