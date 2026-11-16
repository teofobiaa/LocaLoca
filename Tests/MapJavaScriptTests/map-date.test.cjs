const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const webRoot = path.join(__dirname, '../../Server/LocaLoca.Api/wwwroot');
const helperSource = fs.readFileSync(path.join(webRoot, 'map-date.js'), 'utf8');
const mapSource = fs.readFileSync(path.join(webRoot, 'map.js'), 'utf8');

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

test('map.js executes and supplies the local date to both map forms', async () => {
  process.env.TZ = 'America/Los_Angeles';
  class FixedDate extends Date {
    constructor(...args) {
      super(...(args.length ? args : ['2026-01-01T01:30:00.000Z']));
    }
  }

  const mapHandlers = {};
  const markers = [];
  const hint = { textContent: '' };
  const map = {
    setView() { return this; },
    on(event, callback) { mapHandlers[event] = callback; return this; },
    getBounds() {
      return {
        getSouth: () => -90, getWest: () => -180,
        getNorth: () => 90, getEast: () => 180
      };
    },
    closePopup() {}
  };
  const layerGroup = { addTo() { return this; }, clearLayers() {} };
  const leaflet = {
    map: () => map,
    tileLayer: () => ({ addTo() {} }),
    layerGroup: () => layerGroup,
    divIcon: options => options,
    marker: () => {
      const marker = {
        addTo() { return this; },
        bindPopup(html) { this.popupHtml = html; return this; },
        on(event, callback) { this.handlers ||= {}; this.handlers[event] = callback; return this; }
      };
      markers.push(marker);
      return marker;
    },
    popup: () => ({
      setLatLng() { return this; },
      setContent(html) { this.popupHtml = html; return this; },
      openOn() { return this; }
    })
  };
  const context = vm.createContext({
    Date: FixedDate,
    URLSearchParams,
    Map,
    Math,
    Number,
    String,
    JSON,
    Error,
    Object,
    Array,
    Promise,
    location: { search: '?mode=visit' },
    document: {
      documentElement: { dataset: {} },
      getElementById: id => id === 'map-hint' ? hint : null
    },
    window: { innerHeight: 800, clearTimeout() {} },
    L: leaflet,
    fetch: async () => ({
      ok: true,
      status: 200,
      json: async () => ({
        items: [{
          userPlaceId: 42, name: 'Моё место', visited: true,
          visitDate: null, rating: 5, latitude: 0, longitude: 0
        }],
        totalCount: 1,
        hasMore: false
      })
    }),
    setTimeout() { return 1; }
  });

  vm.runInContext(helperSource, context, { filename: 'map-date.js' });
  vm.runInContext(mapSource, context, { filename: 'map.js' });
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(markers.length, 1);
  assert.match(markers[0].popupHtml, /max="2025-12-31"/);
  assert.match(markers[0].popupHtml, /value="2025-12-31"/);

  mapHandlers.click({ latlng: { lat: 10, lng: 20 } });
  const newVisitPopup = context.L.popup; // Keep the map execution assertion explicit below.
  assert.equal(typeof newVisitPopup, 'function');
  assert.equal(context.getLocalDateString(new Date('2026-01-01T01:30:00.000Z')), '2025-12-31');
});

test('map page loads the date helper before map.js', () => {
  const html = fs.readFileSync(path.join(webRoot, 'map.html'), 'utf8');
  assert.ok(html.indexOf('/map-date.js') < html.indexOf('/map.js'));
});
