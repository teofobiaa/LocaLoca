const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const helperPath = path.join(__dirname, '../../Server/LocaLoca.Api/wwwroot/map-date.js');
const helperSource = fs.readFileSync(helperPath, 'utf8');

test('local date uses the browser timezone, not UTC', () => {
  process.env.TZ = 'America/Los_Angeles';
  const context = vm.createContext({});
  vm.runInContext(helperSource, context, { filename: 'map-date.js' });

  // 01:30 UTC on January 1 is still December 31 in Los Angeles.
  const instant = new Date('2026-01-01T01:30:00.000Z');
  assert.equal(context.getLocalDateString(instant), '2025-12-31');
});

test('local date pads month and day with zeroes', () => {
  const context = vm.createContext({});
  vm.runInContext(helperSource, context, { filename: 'map-date.js' });
  const localDate = new Date(2026, 0, 5, 12, 0, 0);
  assert.equal(context.getLocalDateString(localDate), '2026-01-05');
});
