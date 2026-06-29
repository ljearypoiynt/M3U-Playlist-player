var state = {
  sessionId: new URLSearchParams(window.location.search).get('session') || '',
  kind: 'live',
  selectedListId: localStorage.getItem('remoteSelectedListId') || 'all',
  selectedCategory: localStorage.getItem('remoteSelectedCategory') || '',
  curatedLists: [],
  categories: [],
  items: [],
  hasMore: false,
  isLoading: false,
  requestId: 0,
  selected: null,
  guideLoaded: {},
  guideLoading: {},
  editorItems: [],
  editorHasMore: false,
  editorIsLoading: false,
  editorRequestId: 0,
  editorSelectedIds: {}
};

var pageSize = 80;
var editorPageSize = 80;
var guidePageSize = 12;
var searchTimer = null;
var listSearchTimer = null;
var deferredInstallPrompt = null;
var installPromptAvailable = false;

var els = {
  remoteManifest: document.getElementById('remoteManifest'),
  installApp: document.getElementById('installApp'),
  statusPill: document.getElementById('statusPill'),
  liveMode: document.getElementById('liveMode'),
  moviesMode: document.getElementById('moviesMode'),
  search: document.getElementById('search'),
  curatedListSelect: document.getElementById('curatedListSelect'),
  categorySelect: document.getElementById('categorySelect'),
  newList: document.getElementById('newList'),
  stop: document.getElementById('stop'),
  selectedTitle: document.getElementById('selectedTitle'),
  selectedMeta: document.getElementById('selectedMeta'),
  resultCount: document.getElementById('resultCount'),
  results: document.getElementById('results'),
  more: document.getElementById('more'),
  refresh: document.getElementById('refresh'),
  listEditor: document.getElementById('listEditor'),
  closeListEditor: document.getElementById('closeListEditor'),
  listName: document.getElementById('listName'),
  listSearch: document.getElementById('listSearch'),
  listSelectedCount: document.getElementById('listSelectedCount'),
  listEditorItems: document.getElementById('listEditorItems'),
  listMore: document.getElementById('listMore'),
  saveList: document.getElementById('saveList')
};

updateManifestLink();

window.addEventListener('beforeinstallprompt', function (event) {
  event.preventDefault();
  deferredInstallPrompt = event;
  installPromptAvailable = true;
  updateInstallButton();
});

window.addEventListener('appinstalled', function () {
  deferredInstallPrompt = null;
  installPromptAvailable = false;
  updateInstallButton();
});

if (window.matchMedia) {
  var standaloneDisplayQuery = window.matchMedia('(display-mode: standalone)');
  if (standaloneDisplayQuery.addEventListener) {
    standaloneDisplayQuery.addEventListener('change', updateInstallButton);
  } else if (standaloneDisplayQuery.addListener) {
    standaloneDisplayQuery.addListener(updateInstallButton);
  }
}

els.installApp.addEventListener('click', function () {
  if (!deferredInstallPrompt) {
    setStatus('Browser menu');
    return;
  }

  var promptEvent = deferredInstallPrompt;
  deferredInstallPrompt = null;
  installPromptAvailable = false;

  promptEvent.prompt();
  promptEvent.userChoice.finally(function () {
    deferredInstallPrompt = null;
    updateInstallButton();
  });
});

els.liveMode.addEventListener('click', function () {
  setKind('live');
});
els.moviesMode.addEventListener('click', function () {
  setKind('movies');
});
els.search.addEventListener('input', function () {
  window.clearTimeout(searchTimer);
  searchTimer = window.setTimeout(loadMedia, 240);
});
els.curatedListSelect.addEventListener('change', function () {
  state.selectedListId = els.curatedListSelect.value || 'all';
  localStorage.setItem('remoteSelectedListId', state.selectedListId);
  loadMedia();
});
els.categorySelect.addEventListener('change', function () {
  state.selectedCategory = els.categorySelect.value || '';
  localStorage.setItem('remoteSelectedCategory', state.selectedCategory);
  loadMedia();
});
els.newList.addEventListener('click', openListEditor);
els.closeListEditor.addEventListener('click', closeListEditor);
els.listSearch.addEventListener('input', function () {
  window.clearTimeout(listSearchTimer);
  listSearchTimer = window.setTimeout(loadEditorMedia, 220);
});
els.listMore.addEventListener('click', loadMoreEditorMedia);
els.saveList.addEventListener('click', saveCuratedList);
els.stop.addEventListener('click', function () {
  if (!ensureSession()) {
    return;
  }

  sendCommand({ type: 'stop' })
    .then(function () {
      setStatus('Stop sent', false);
      els.selectedTitle.textContent = 'Playback stopped';
      els.selectedMeta.textContent = 'The TV should return to the guide preview.';
    })
    .catch(function (error) {
      setStatus(error.message, true);
    });
});
els.more.addEventListener('click', loadMoreMedia);
els.refresh.addEventListener('click', function () {
  loadCuratedLists();
  loadCategories().then(loadMedia);
});

function setKind(kind) {
  state.kind = kind;
  state.selected = null;
  state.selectedCategory = '';
  localStorage.setItem('remoteSelectedCategory', '');
  updateButtons();
  loadCuratedLists();
  loadCategories().then(loadMedia);
}

function updateButtons() {
  els.liveMode.classList.toggle('active', state.kind === 'live');
  els.moviesMode.classList.toggle('active', state.kind === 'movies');
}

function loadStatus() {
  if (!ensureSession()) {
    return Promise.resolve();
  }

  return fetchJson(apiPath('/status'))
    .then(function (status) {
      setStatus('Connected', false);
      if (status.deviceName) {
        els.selectedMeta.textContent = 'Controlling ' + status.deviceName + '.';
      }
    })
    .catch(function (error) {
      setStatus(error.message, true);
    });
}

function loadCuratedLists() {
  if (!ensureSession()) {
    return Promise.resolve();
  }

  return fetchJson(apiPath('/curated-lists?kind=' + encodeURIComponent(state.kind)))
    .then(function (data) {
      state.curatedLists = data.lists || [];
      if (!findCuratedList(state.selectedListId)) {
        state.selectedListId = 'all';
        localStorage.setItem('remoteSelectedListId', state.selectedListId);
      }
      renderCuratedLists();
    })
    .catch(function () {
      state.selectedListId = 'all';
      localStorage.setItem('remoteSelectedListId', state.selectedListId);
      state.curatedLists = getFallbackCuratedLists();
      renderCuratedLists();
    });
}

function loadCategories() {
  if (!ensureSession()) {
    return Promise.resolve();
  }

  return fetchJson(apiPath('/categories?kind=' + encodeURIComponent(state.kind) + '&keptOnly=true'))
    .then(function (data) {
      state.categories = data.categories || [];
      if (state.categories.indexOf(state.selectedCategory) === -1) {
        state.selectedCategory = '';
        localStorage.setItem('remoteSelectedCategory', '');
      }
      renderCategories();
    })
    .catch(function () {
      state.categories = [];
      state.selectedCategory = '';
      localStorage.setItem('remoteSelectedCategory', '');
      renderCategories();
    });
}

function renderCategories() {
  var option = document.createElement('option');
  var index;

  els.categorySelect.innerHTML = '';
  option.value = '';
  option.textContent = 'All kept categories';
  els.categorySelect.appendChild(option);

  for (index = 0; index < state.categories.length; index += 1) {
    option = document.createElement('option');
    option.value = state.categories[index];
    option.textContent = state.categories[index];
    els.categorySelect.appendChild(option);
  }

  els.categorySelect.value = state.categories.indexOf(state.selectedCategory) >= 0
    ? state.selectedCategory
    : '';
}

function getFallbackCuratedLists() {
  var lists = [
    {
      id: 'all',
      name: state.kind === 'live' ? 'All channels' : 'All movies',
      count: null
    }
  ];

  if (state.kind === 'live') {
    lists.push({
      id: 'builtin-uk',
      name: 'UK Essentials',
      count: null
    });
  }

  return lists;
}

function renderCuratedLists() {
  els.curatedListSelect.innerHTML = '';
  if (state.curatedLists.length === 0) {
    appendCuratedListOption({
      id: 'all',
      name: state.kind === 'live' ? 'All channels' : 'All movies',
      count: null
    });
    return;
  }

  for (var index = 0; index < state.curatedLists.length; index += 1) {
    appendCuratedListOption(state.curatedLists[index]);
  }

  els.curatedListSelect.value = findCuratedList(state.selectedListId) ? state.selectedListId : 'all';
}

function appendCuratedListOption(list) {
  var option = document.createElement('option');
  var count = typeof list.count === 'number' ? ' (' + list.count + ')' : '';

  option.value = list.id || 'all';
  option.textContent = list.name + count;
  els.curatedListSelect.appendChild(option);
}

function findCuratedList(id) {
  for (var index = 0; index < state.curatedLists.length; index += 1) {
    if (state.curatedLists[index].id === id) {
      return state.curatedLists[index];
    }
  }

  return null;
}

function loadMedia() {
  if (!ensureSession()) {
    return Promise.resolve();
  }

  state.requestId += 1;
  state.items = [];
  state.hasMore = false;
  state.guideLoaded = {};
  state.guideLoading = {};
  els.results.innerHTML = '';
  els.more.hidden = true;
  setResultCount('Loading...');
  return loadMediaPage(0, state.requestId);
}

function loadMoreMedia() {
  if (!state.hasMore || state.isLoading) {
    return;
  }

  loadMediaPage(state.items.length, state.requestId);
}

function loadMediaPage(skip, requestId) {
  var params = new URLSearchParams({
    kind: state.kind,
    query: els.search.value.trim(),
    skip: String(skip),
    limit: String(pageSize)
  });

  if (state.selectedCategory) {
    params.set('group', state.selectedCategory);
  }

  if (state.selectedListId && state.selectedListId !== 'all') {
    params.set('list', state.selectedListId);
  }

  state.isLoading = true;
  els.more.hidden = true;

  return fetchJson(apiPath('/media?' + params.toString()))
    .then(function (data) {
      var newItems = data.items || [];
      if (requestId !== state.requestId) {
        return;
      }

      if (skip > 0) {
        state.items = state.items.concat(newItems);
        appendItems(newItems, skip);
      } else {
        state.items = newItems;
        renderItems();
      }

      state.hasMore = !!data.hasMore;
      els.more.hidden = !state.hasMore;
      setResultCount(state.items.length + (state.hasMore ? '+' : '') + ' shown');
      loadGuideForItems(newItems, requestId);
    })
    .catch(function (error) {
      if (requestId === state.requestId) {
        if (state.selectedListId !== 'all') {
          state.selectedListId = 'all';
          localStorage.setItem('remoteSelectedListId', state.selectedListId);
          renderCuratedLists();
          setResultCount('List failed, showing all');
          loadMedia();
          return;
        }

        setResultCount('Load failed');
        setStatus(error.message, true);
      }
    })
    .then(function () {
      if (requestId === state.requestId) {
        state.isLoading = false;
      }
    });
}

function renderItems() {
  els.results.innerHTML = '';
  if (state.items.length === 0) {
    els.results.innerHTML = '<p class="empty">No matches yet.</p>';
    return;
  }

  appendItems(state.items, 0);
}

function appendItems(items, startIndex) {
  for (var index = 0; index < items.length; index += 1) {
    appendItem(items[index], startIndex + index);
  }
}

function appendItem(item, index) {
  var button = document.createElement('button');
  var image = item.icon ? document.createElement('img') : document.createElement('div');
  var body = document.createElement('span');
  var title = document.createElement('span');
  var meta = document.createElement('span');
  var guide = document.createElement('span');

  button.className = 'item';
  button.type = 'button';
  button.dataset.index = String(index);
  button.dataset.id = item.id;
  button.addEventListener('click', function () {
    playItem(item);
  });

  if (item.icon) {
    image.src = item.icon;
    image.alt = '';
  } else {
    image.className = 'logo-fallback';
    image.textContent = initials(item.name);
  }

  body.className = 'item-body';
  title.className = 'item-title';
  meta.className = 'item-meta';
  guide.className = 'item-guide';
  title.textContent = item.name;
  meta.textContent = item.group || 'Ungrouped';
  setGuideText(guide, item);

  body.appendChild(title);
  body.appendChild(meta);
  body.appendChild(guide);
  button.appendChild(image);
  button.appendChild(body);
  els.results.appendChild(button);
}

function openListEditor() {
  if (!ensureSession()) {
    return;
  }

  state.editorSelectedIds = {};
  state.editorItems = [];
  state.editorHasMore = false;
  state.editorRequestId += 1;
  els.listName.value = '';
  els.listSearch.value = '';
  updateEditorSelectedCount();
  document.body.classList.add('editor-open');
  els.listEditor.hidden = false;
  loadEditorMedia();
  window.setTimeout(function () {
    els.listName.focus();
  }, 50);
}

function closeListEditor() {
  els.listEditor.hidden = true;
  document.body.classList.remove('editor-open');
}

function loadEditorMedia() {
  state.editorRequestId += 1;
  state.editorItems = [];
  state.editorHasMore = false;
  els.listEditorItems.innerHTML = '';
  els.listMore.hidden = true;
  return loadEditorMediaPage(0, state.editorRequestId);
}

function loadMoreEditorMedia() {
  if (!state.editorHasMore || state.editorIsLoading) {
    return;
  }

  loadEditorMediaPage(state.editorItems.length, state.editorRequestId);
}

function loadEditorMediaPage(skip, requestId) {
  var params = new URLSearchParams({
    kind: state.kind,
    query: els.listSearch.value.trim(),
    skip: String(skip),
    limit: String(editorPageSize)
  });

  state.editorIsLoading = true;
  els.listMore.hidden = true;

  return fetchJson(apiPath('/media?' + params.toString()))
    .then(function (data) {
      var newItems = data.items || [];
      if (requestId !== state.editorRequestId) {
        return;
      }

      if (skip > 0) {
        state.editorItems = state.editorItems.concat(newItems);
        appendEditorItems(newItems);
      } else {
        state.editorItems = newItems;
        renderEditorItems();
      }

      state.editorHasMore = !!data.hasMore;
      els.listMore.hidden = !state.editorHasMore;
    })
    .catch(function (error) {
      if (requestId === state.editorRequestId) {
        els.listEditorItems.innerHTML = '<p class="empty">Load failed: ' + error.message + '</p>';
      }
    })
    .then(function () {
      if (requestId === state.editorRequestId) {
        state.editorIsLoading = false;
      }
    });
}

function renderEditorItems() {
  els.listEditorItems.innerHTML = '';
  if (state.editorItems.length === 0) {
    els.listEditorItems.innerHTML = '<p class="empty">No channels found.</p>';
    return;
  }

  appendEditorItems(state.editorItems);
}

function appendEditorItems(items) {
  for (var index = 0; index < items.length; index += 1) {
    appendEditorItem(items[index]);
  }
}

function appendEditorItem(item) {
  var button = document.createElement('button');
  var checkbox = document.createElement('span');
  var image = item.icon ? document.createElement('img') : document.createElement('div');
  var body = document.createElement('span');
  var title = document.createElement('span');
  var meta = document.createElement('span');
  var guide = document.createElement('span');

  button.className = 'item editor-select-item';
  button.type = 'button';
  button.classList.toggle('selected', !!state.editorSelectedIds[item.id]);
  button.addEventListener('click', function () {
    toggleEditorItem(item.id, button);
  });

  checkbox.className = 'editor-checkbox';
  checkbox.textContent = state.editorSelectedIds[item.id] ? 'x' : '';

  if (item.icon) {
    image.src = item.icon;
    image.alt = '';
  } else {
    image.className = 'logo-fallback';
    image.textContent = initials(item.name);
  }

  body.className = 'item-body';
  title.className = 'item-title';
  meta.className = 'item-meta';
  guide.className = 'item-guide';
  title.textContent = item.name;
  meta.textContent = item.group || 'Ungrouped';
  var guideTitle = getProgrammeDisplayTitle(item, 'now');

  guide.textContent = guideTitle === 'Unknown' ? '' : guideTitle;

  body.appendChild(title);
  body.appendChild(meta);
  body.appendChild(guide);
  button.appendChild(checkbox);
  button.appendChild(image);
  button.appendChild(body);
  els.listEditorItems.appendChild(button);
}

function toggleEditorItem(id, button) {
  if (state.editorSelectedIds[id]) {
    delete state.editorSelectedIds[id];
  } else {
    state.editorSelectedIds[id] = true;
  }

  button.classList.toggle('selected', !!state.editorSelectedIds[id]);
  button.querySelector('.editor-checkbox').textContent = state.editorSelectedIds[id] ? 'x' : '';
  updateEditorSelectedCount();
}

function updateEditorSelectedCount() {
  var count = Object.keys(state.editorSelectedIds).length;
  els.listSelectedCount.textContent = count + ' selected';
}

function saveCuratedList() {
  var ids = Object.keys(state.editorSelectedIds);
  var name = els.listName.value.trim();

  if (!name) {
    setStatus('Name needed', true);
    els.listName.focus();
    return;
  }

  if (ids.length === 0) {
    setStatus('Pick channels', true);
    return;
  }

  fetch(apiPath('/curated-lists'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      kind: state.kind,
      name: name,
      itemIds: ids
    })
  })
    .then(function (response) {
      if (!response.ok) {
        return response.text().then(function (body) {
          throw new Error(formatError(response.status, body));
        });
      }

      return response.json();
    })
    .then(function (result) {
      var list = result.list || result.List;
      if (list && list.id) {
        state.selectedListId = list.id;
        localStorage.setItem('remoteSelectedListId', state.selectedListId);
      }
      closeListEditor();
      return loadCuratedLists();
    })
    .then(function () {
      setStatus('List saved', false);
      return loadMedia();
    })
    .catch(function (error) {
      setStatus(error.message, true);
    });
}

function playItem(item) {
  if (!ensureSession()) {
    return;
  }

  state.selected = item;
  els.selectedTitle.textContent = getProgrammeDisplayTitle(item, 'now') === 'Unknown'
    ? item.name
    : getProgrammeDisplayTitle(item, 'now');
  els.selectedMeta.textContent = buildSelectedMeta(item);

  sendCommand({
    type: 'play',
    itemId: item.id,
    kind: state.kind,
    item: item
  })
    .then(function () {
      setStatus('Play sent', false);
    })
    .catch(function (error) {
      setStatus(error.message, true);
    });
}

function sendCommand(command) {
  return fetch(apiPath('/remote/command'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(command)
  }).then(function (response) {
    if (!response.ok) {
      return response.text().then(function (body) {
        throw new Error(formatError(response.status, body));
      });
    }

    return response.json();
  });
}

function loadGuideForItems(items, requestId) {
  var ids = [];
  var index;

  if (state.kind !== 'live') {
    return;
  }

  for (index = 0; index < items.length; index += 1) {
    if (items[index].id && !state.guideLoaded[items[index].id] && !state.guideLoading[items[index].id]) {
      ids.push(items[index].id);
      state.guideLoading[items[index].id] = true;
    }
  }

  requestGuideChunk(ids, 0, requestId, 'main');
}

function requestGuideChunk(ids, offset, requestId, source) {
  var chunk;
  var guideSource = source || 'main';

  if (requestId !== state.requestId || offset >= ids.length) {
    return;
  }

  chunk = ids.slice(offset, offset + guidePageSize);
  fetchJson(apiPath('/guide?source=' + encodeURIComponent(guideSource) + '&ids=' + encodeURIComponent(chunk.join(','))))
    .then(function (data) {
      var guide = data.guide || {};
      var serverMissingIds = data.missingIds || data.MissingIds || [];
      var missingLookup = {};
      var shortIds = [];
      var index;
      var item;
      var info;
      var isMissing;

      if (requestId !== state.requestId) {
        return;
      }

      for (index = 0; index < serverMissingIds.length; index += 1) {
        missingLookup[serverMissingIds[index]] = true;
      }

      for (index = 0; index < chunk.length; index += 1) {
        item = findItemById(chunk[index]);
        info = guide[chunk[index]];
        isMissing = missingLookup[chunk[index]] || !info || isMissingGuideInfo(info);

        if (guideSource === 'main' && isMissing) {
          shortIds.push(chunk[index]);
        } else {
          state.guideLoaded[chunk[index]] = true;
          state.guideLoading[chunk[index]] = false;
        }

        if (item && info && !isMissing) {
          applyGuide(item, info);
        }

        if (item) {
          updateItemGuide(item);
        }
      }

      if (guideSource === 'main' && shortIds.length > 0) {
        requestGuideChunk(shortIds, 0, requestId, 'short');
      }
    })
    .catch(function () {
      var index;

      for (index = 0; index < chunk.length; index += 1) {
        state.guideLoading[chunk[index]] = false;
      }
    })
    .then(function () {
      requestGuideChunk(ids, offset + guidePageSize, requestId, guideSource);
    });
}

function isMissingGuideInfo(info) {
  if (!info) {
    return true;
  }

  return !(info.nowTitle || info.NowTitle || info.nextTitle || info.NextTitle);
}

function applyGuide(item, info) {
  item.nowTitle = info.nowTitle || info.NowTitle || item.nowTitle;
  item.nextTitle = info.nextTitle || info.NextTitle || item.nextTitle;
  item.nowDescription = info.nowDescription || info.NowDescription || item.nowDescription;
  item.nextDescription = info.nextDescription || info.NextDescription || item.nextDescription;
  item.nowStart = info.nowStart || info.NowStart || item.nowStart;
  item.nowEnd = info.nowEnd || info.NowEnd || item.nowEnd;
  item.nextStart = info.nextStart || info.NextStart || item.nextStart;
  item.nextEnd = info.nextEnd || info.NextEnd || item.nextEnd;
}

function updateItemGuide(item) {
  var node = els.results.querySelector('[data-id="' + cssEscape(item.id) + '"] .item-guide');
  if (node) {
    setGuideText(node, item);
  }

  if (state.selected && state.selected.id === item.id) {
    els.selectedTitle.textContent = getProgrammeDisplayTitle(item, 'now') === 'Unknown'
      ? item.name
      : getProgrammeDisplayTitle(item, 'now');
    els.selectedMeta.textContent = buildSelectedMeta(item);
  }
}

function findItemById(id) {
  for (var index = 0; index < state.items.length; index += 1) {
    if (state.items[index].id === id) {
      return state.items[index];
    }
  }

  return null;
}

function setGuideText(node, item) {
  var nowTitle = getProgrammeDisplayTitle(item, 'now');
  var nowTime = getProgrammeTime(item, 'now');
  var nextTitle = getProgrammeDisplayTitle(item, 'next');

  if (nowTitle === 'Unknown' && nextTitle === 'Unknown') {
    node.textContent = 'Guide unavailable';
    return;
  }

  node.innerHTML = '<strong></strong> ' + escapeHtml(nowTime || '') + ' - Next: ' + escapeHtml(nextTitle);
  node.querySelector('strong').textContent = nowTitle;
}

function buildSelectedMeta(item) {
  var parts = [];
  var nowTime = getProgrammeTime(item, 'now');
  var nextTitle = getProgrammeDisplayTitle(item, 'next');
  var nextTime = getProgrammeTime(item, 'next');
  var description = item.nowDescription || item.description || '';

  parts.push(item.name + ' - ' + (item.group || 'Ungrouped'));
  if (nowTime) {
    parts.push(nowTime);
  }
  if (description) {
    parts.push(truncateText(description, 150));
  }
  if (nextTitle !== 'Unknown' && nextTitle !== 'pending...') {
    parts.push('Next: ' + nextTitle + (nextTime ? ' ' + nextTime : ''));
  }

  return parts.join('\n');
}

function getProgrammeTitle(item, slot) {
  var value = slot === 'now'
    ? item.nowTitle || item.now || item.current || item.currentTitle
    : item.nextTitle || item.next;

  if (value && value.title) {
    return value.title;
  }

  return value || 'Unknown';
}

function getProgrammeDisplayTitle(item, slot) {
  var title = getProgrammeTitle(item, slot);

  if (title === 'Unknown' && isGuidePending(item)) {
    return 'pending...';
  }

  return title;
}

function isGuidePending(item) {
  return state.kind === 'live' &&
    item &&
    item.id &&
    item.id.indexOf('live-') === 0 &&
    !state.guideLoaded[item.id];
}

function getProgrammeTime(item, slot) {
  var start = slot === 'now'
    ? item.nowStart || (item.now && item.now.start)
    : item.nextStart || (item.next && item.next.start);
  var end = slot === 'now'
    ? item.nowEnd || (item.now && item.now.end)
    : item.nextEnd || (item.next && item.next.end);

  if (start && end) {
    return start + ' - ' + end;
  }

  return start || end || '';
}

function truncateText(value, maxLength) {
  if (!value || value.length <= maxLength) {
    return value || '';
  }

  return value.slice(0, maxLength - 1).trim() + '...';
}

function initials(value) {
  var words = String(value || '').trim().split(/\s+/).slice(0, 2);
  return words.map(function (word) {
    return word.charAt(0);
  }).join('').toUpperCase() || 'TV';
}

function fetchJson(path) {
  return fetch(path)
    .then(function (response) {
      if (!response.ok) {
        return response.text().then(function (body) {
          throw new Error(formatError(response.status, body));
        });
      }

      return response.json();
    });
}

function apiPath(path) {
  return '/api/sessions/' + encodeURIComponent(state.sessionId) + path;
}

function ensureSession() {
  if (state.sessionId) {
    return true;
  }

  setStatus('No session', true);
  setResultCount('No session');
  els.selectedTitle.textContent = 'Open from the TV remote link';
  els.selectedMeta.textContent = 'This phone remote needs a session link from the LG app.';
  return false;
}

function setResultCount(message) {
  els.resultCount.textContent = message;
}

function setStatus(message, isError) {
  els.statusPill.textContent = message.length > 18 ? message.slice(0, 18) : message;
  els.statusPill.classList.toggle('error', !!isError);
}

function formatError(status, body) {
  var message = 'Request failed ' + status;
  if (!body) {
    return message;
  }

  try {
    var parsed = JSON.parse(body);
    return parsed.error || parsed.detail || message;
  } catch (error) {
    return body.slice(0, 100) || message;
  }
}

function escapeHtml(value) {
  return String(value || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function cssEscape(value) {
  if (window.CSS && window.CSS.escape) {
    return window.CSS.escape(value);
  }

  return String(value).replace(/"/g, '\\"');
}

function updateManifestLink() {
  if (!els.remoteManifest) {
    return;
  }

  els.remoteManifest.href = '/remote/manifest.webmanifest?session=' + encodeURIComponent(state.sessionId || '');
}

function isStandaloneApp() {
  return window.matchMedia('(display-mode: standalone)').matches ||
    window.navigator.standalone === true;
}

function updateInstallButton() {
  if (!els.installApp) {
    return;
  }

  els.installApp.hidden = isStandaloneApp();
  els.installApp.textContent = 'Install';
  els.installApp.title = installPromptAvailable
    ? 'Install IPTV Sidekick on this device'
    : 'Use the browser install option if the prompt does not open';
}

if ('serviceWorker' in navigator) {
  window.addEventListener('load', function () {
    navigator.serviceWorker.register('/remote-sw.js', {
      scope: '/'
    }).catch(function () {
    });
  });
}

updateInstallButton();
updateButtons();
if (ensureSession()) {
  loadStatus();
  loadCuratedLists();
  loadCategories().then(loadMedia);
}
