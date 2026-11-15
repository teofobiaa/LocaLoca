// Remove legacy credential fragments before Leaflet or application scripts run.
// Fragments are not an authentication channel; the native WebView2 host adds
// Authorization headers to the map API requests.
if (location.hash) {
  history.replaceState(null, '', location.pathname + location.search);
}
