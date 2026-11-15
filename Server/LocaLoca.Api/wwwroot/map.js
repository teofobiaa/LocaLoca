const params = new URLSearchParams(location.search);
document.documentElement.dataset.theme = params.get('theme') === 'dark' ? 'dark' : 'light';
const startNewVisit = params.get('mode') === 'visit';
const map = L.map('map').setView([48, 16], 4);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
}).addTo(map);
const palette = { visited:'#26966f', wished:'#d59b2a', normal:'#4777c8' };
const markers = L.layerGroup().addTo(map);
const photoUrls = new Map();
async function api(path, options = {}) {
  const response = await fetch(path, { ...options, credentials: 'omit', headers: { ...(options.headers || {}) } });
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new Error(body?.error || 'Не удалось сохранить данные.');
  }
  return response.status === 204 ? null : response.json();
}
function markerIcon(color) {
  const pinClass = color === palette.visited ? 'visited' : (color === palette.wished ? 'wished' : 'normal');
  return L.divIcon({ className:'', html:`<div class="pin ${pinClass}"></div>`, iconSize:[24,24], iconAnchor:[12,12] });
}
function statusControls(status) {
  return `<div class="status-switch"><label><input type="radio" name="status" value="planned" ${status === 'planned' ? 'checked' : ''}>В планах</label><label><input type="radio" name="status" value="visited" ${status === 'visited' ? 'checked' : ''}>Посещено</label></div>`;
}
function getStatus(form) { return form.querySelector('input[name="status"]:checked').value; }
function getRating(form) {
  const selected = form.querySelector('input[name="rating"]:checked');
  return selected ? Number(selected.value) : null;
}
function ratingControls(selectedRating = null) {
  return `<div class="rating-switch" role="radiogroup" aria-label="Оценка">${[1,2,3,4,5].map(value =>
    `<label class="rating-option"><input type="radio" name="rating" value="${value}" ${Number(selectedRating) === value ? 'checked' : ''}><span>${value}</span></label>`
  ).join('')}</div>`;
}
function bindStatusFields(form) {
  const update = () => {
    const visited = getStatus(form) === 'visited';
    form.elements.date.disabled = !visited;
    form.querySelectorAll('input[name="rating"]').forEach(input => { input.disabled = !visited; });
  };
  form.querySelectorAll('input[name="status"]').forEach(input => input.addEventListener('change', update));
  update();
}
function popupOptions() {
  return {
    maxWidth: 320,
    maxHeight: Math.max(140, Math.floor(window.innerHeight * 0.64)),
    autoPan: true,
    autoPanPadding: [18, 18]
  };
}
function bindPlaceEditor(marker, item) {
  const id = item.userPlaceId;
  const date = item.visitDate || today;
  marker.bindPopup(`<form class="place-form" id="edit-${id}">
    <strong>Моё место</strong><input name="name" maxlength="150" required value="${escapeHtml(item.name)}" aria-label="Название места">
    ${statusControls(item.visited ? 'visited' : 'planned')}
    <label class="field-label">Дата посещения</label><input name="date" type="date" max="${today}" value="${date}"><label class="field-label">Оценка</label>        ${ratingControls(item.rating)}
    <textarea name="impressions" maxlength="10000" placeholder="Впечатления">${escapeHtml(item.impressions || '')}</textarea>
    <textarea name="notes" maxlength="10000" placeholder="Личные заметки">${escapeHtml(item.notes || '')}</textarea>
    <button type="submit">Сохранить место</button>
    <label>Фотографии <input id="files-${id}" type="file" accept="image/*" multiple></label><button type="button" id="upload-${id}">Загрузить фото</button>
    <div class="place-photos" id="photos-${id}"></div><button class="danger" type="button" id="delete-${id}">Удалить место</button>
  </form>`, popupOptions());
  marker.on('popupopen', () => {
    const form = document.getElementById(`edit-${id}`);
    bindStatusFields(form);
    form.addEventListener('submit', async event => {
      event.preventDefault();
      try {
        await api(`/api/me/map-places/${id}`, { method:'PUT', headers:{ 'Content-Type':'application/json' }, body:JSON.stringify({
          name:form.elements.name.value, status:getStatus(form), visitDate:form.elements.date.value, rating:getRating(form),
          impressions:form.elements.impressions.value, notes:form.elements.notes.value
        }) });
        await loadMarkers();
      } catch (error) { alert(error.message); }
    });
    document.getElementById(`upload-${id}`).addEventListener('click', async () => {
      const files = document.getElementById(`files-${id}`).files;
      if (!files.length) { alert('Сначала выберите фотографии.'); return; }
      const data = new FormData();
      for (const file of files) data.append('photos', file);
      try { await api(`/api/me/map-places/${id}/photos`, { method:'POST', body:data }); await showPhotos(id); }
      catch (error) { alert(error.message); }
    });
    document.getElementById(`delete-${id}`).addEventListener('click', async () => {
      if (!confirm('Удалить место и связанные фотографии?')) return;
      try { await api(`/api/me/map-places/${id}`, { method:'DELETE' }); await loadMarkers(); }
      catch (error) { alert(error.message); }
    });
    showPhotos(id);
  });
}
async function uploadPlacePhotos(id, files) {
  if (!files.length) return;
  const data = new FormData();
  for (const file of files) data.append('photos', file);
  await api(`/api/me/map-places/${id}/photos`, { method:'POST', body:data });
}
async function showPhotos(id) {
  const host = document.getElementById(`photos-${id}`);
  if (!host) return;
  try {
    const data = await api(`/api/me/map-places/${id}/photos`);
    host.replaceChildren();
    for (const url of photoUrls.get(id) || []) URL.revokeObjectURL(url);
    photoUrls.set(id, []);
    for (const photo of data.items) {
      const response = await fetch(`/api/me/map-place-photos/${photo.id}`, { credentials: 'omit' });
      if (!response.ok) continue;
      const url = URL.createObjectURL(await response.blob());
      photoUrls.get(id).push(url);
      const row = document.createElement('div');
      const image = document.createElement('img');
      image.src = url; image.alt = photo.caption || 'Фотография'; image.className = 'place-photo-image';
      const caption = document.createElement('span');
      caption.textContent = photo.caption || 'Фотография';
      const remove = document.createElement('button');
      remove.type = 'button'; remove.className = 'danger'; remove.textContent = 'Удалить';
      remove.addEventListener('click', async () => { try { await api(`/api/me/map-place-photos/${photo.id}`, { method:'DELETE' }); await showPhotos(id); } catch (error) { alert(error.message); } });
      row.append(image, caption, remove); host.append(row);
    }
  } catch (error) { host.textContent = error.message; }
}
map.on('click', event => {
  const lat = event.latlng.lat.toFixed(6), lon = event.latlng.lng.toFixed(6);
  const defaultStatus = startNewVisit ? 'visited' : 'planned';
  const date = startNewVisit ? today : '';
  const popup = L.popup(popupOptions()).setLatLng(event.latlng).setContent(`<form class="place-form" id="new-place">
    <strong>${startNewVisit ? 'Новое посещение' : 'Добавить своё место'}</strong>
    <input name="name" maxlength="150" placeholder="Название места" required>
    ${statusControls(defaultStatus)}
    <label class="field-label">Дата посещения</label><input name="date" type="date" max="${today}" value="${date}"><label class="field-label">Оценка</label>
    ${ratingControls(startNewVisit ? 5 : null)}
    <textarea name="impressions" maxlength="10000" placeholder="Впечатления"></textarea>
    <textarea name="notes" maxlength="10000" placeholder="Личные заметки"></textarea>
    <label>Фотографии <input name="photos" type="file" accept="image/*" multiple></label>
    <button type="submit">${defaultStatus === 'visited' ? 'Сохранить посещение' : 'Сохранить в планы'}</button>
  </form>`).openOn(map);
  setTimeout(() => {
    const form = document.getElementById('new-place');
    if (!form) return;
    bindStatusFields(form);
    form.querySelectorAll('input[name="status"]').forEach(input => input.addEventListener('change', () => {
      form.querySelector('button[type="submit"]').textContent = getStatus(form) === 'visited' ? 'Сохранить посещение' : 'Сохранить в планы';
    }));
    form.addEventListener('submit', async submitEvent => {
      submitEvent.preventDefault();
      const status = getStatus(form);
      if (status === 'visited' && (!form.elements.date.value || getRating(form) === null)) { alert('Укажите дату и оценку посещения.'); return; }
      let place;
      try {
        place = await api('/api/me/map-places', { method:'POST', headers:{ 'Content-Type':'application/json' }, body:JSON.stringify({
          name:form.elements.name.value, latitude:Number(lat), longitude:Number(lon), status,
          visitDate:form.elements.date.value, rating:getRating(form),
          impressions:form.elements.impressions.value, notes:form.elements.notes.value
        }) });
      }
      catch (error) { alert(error.message); return; }
      let photoError = '';
      try { await uploadPlacePhotos(place.id, form.elements.photos.files); }
      catch (error) { photoError = error.message; }
      map.closePopup(popup);
      await loadMarkers();
      if (photoError) alert(`Посещение сохранено, но фотографии не добавлены: ${photoError}`);
    });
  }, 0);
});

let markerRequestId = 0;
let mapMoveTimer = null;
const markerPageSize = 500;

function normalizeLongitude(value) {
  return ((value + 180) % 360 + 360) % 360 - 180;
}

async function loadMarkers() {
  const requestId = ++markerRequestId;
  try {
    const bounds = map.getBounds();
    const rawWest = bounds.getWest();
    const rawEast = bounds.getEast();
    const spansWorld = rawEast - rawWest >= 359.999;
    const query = new URLSearchParams({
      south: bounds.getSouth().toFixed(6),
      west: (spansWorld ? -180 : normalizeLongitude(rawWest)).toFixed(6),
      north: bounds.getNorth().toFixed(6),
      east: (spansWorld ? 180 : normalizeLongitude(rawEast)).toFixed(6),
      page: '1',
      pageSize: String(markerPageSize)
    });

    const data = await api('/api/me/map-markers?' + query.toString());
    if (requestId !== markerRequestId) return;

    for (const urls of photoUrls.values())
      for (const url of urls) URL.revokeObjectURL(url);
    photoUrls.clear();
    markers.clearLayers();

    data.items.forEach(item => {
      const color = item.visited ? palette.visited : (item.wished ? palette.wished : palette.normal);
      const marker = L.marker([item.latitude, item.longitude], { icon:markerIcon(color) }).addTo(markers);
      if (item.userPlaceId) bindPlaceEditor(marker, item);
      else marker.bindPopup(
        '<b>' + escapeHtml(item.name) + '</b><br>' +
        (item.visited ? 'Посещено' : (item.wished ? 'Хочу посетить' : 'В каталоге'))
      );
    });

    const hint = document.getElementById('map-hint');
    if (data.hasMore) {
      hint.textContent = 'Показаны первые ' + data.items.length + ' из ' + data.totalCount +
        ' точек в этой области. Приблизьте карту, чтобы сузить выдачу.';
    } else {
      hint.textContent = data.totalCount
        ? 'Точек в видимой области: ' + data.totalCount + '. Нажмите на карту, чтобы добавить своё место'
        : 'В этой области пока нет точек. Нажмите на карту, чтобы добавить своё место';
    }
  } catch (error) {
    if (requestId === markerRequestId)
      document.getElementById('map-hint').textContent = error.message;
  }
}

map.on('moveend', () => {
  window.clearTimeout(mapMoveTimer);
  mapMoveTimer = window.setTimeout(loadMarkers, 180);
});

function escapeHtml(value) { return String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
loadMarkers();