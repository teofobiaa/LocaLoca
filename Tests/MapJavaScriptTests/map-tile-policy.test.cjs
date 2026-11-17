const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../..');
const endpoint = fs.readFileSync(path.join(root, 'Server/LocaLoca.Api/Endpoints/UserMapEndpoints.cs'), 'utf8');
const html = fs.readFileSync(path.join(root, 'Server/LocaLoca.Api/wwwroot/map.html'), 'utf8');
const mapJs = fs.readFileSync(path.join(root, 'Server/LocaLoca.Api/wwwroot/map.js'), 'utf8');

test('map allows the canonical OSM tile host and sends a usable referrer', () => {
  assert.ok(endpoint.includes("img-src 'self' data: blob: https://tile.openstreetmap.org;"));
  assert.ok(endpoint.includes('Referrer-Policy"] = "strict-origin-when-cross-origin"'));
  assert.ok(html.includes('<meta name="referrer" content="strict-origin-when-cross-origin">'));
});

test('map uses the canonical OSM tile endpoint and retains attribution', () => {
  assert.ok(mapJs.includes("L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png'"));
  assert.ok(!mapJs.includes('{s}.tile.openstreetmap.org'));
  assert.ok(mapJs.includes('OpenStreetMap'));
});
