function getDefaultPlaybackMode() {
  var userAgent = String(navigator.userAgent || '').toLowerCase();
  var deviceInfo = String(window.PalmSystem && window.PalmSystem.deviceInfo || '').toLowerCase();

  return userAgent.indexOf('emulator') !== -1 ||
    userAgent.indexOf('simulator') !== -1 ||
    deviceInfo.indexOf('emulator') !== -1 ||
    deviceInfo.indexOf('simulator') !== -1 ||
    window.location.search.indexOf('playback=hls') !== -1
    ? 'hls'
    : 'direct';
}

function readJsonStorage(key, fallback) {
  try {
    return JSON.parse(localStorage.getItem(key) || '') || fallback;
  } catch (error) {
    return fallback;
  }
}

var state = {
  serverUrl: localStorage.getItem('serverUrl') || 'http://192.168.50.99:5055',
  playlistUrl: localStorage.getItem('playlistUrl') || '',
  epgUrl: localStorage.getItem('epgUrl') || '',
  sessionId: localStorage.getItem('sessionId') || null,
  remoteUrl: localStorage.getItem('remoteUrl') || '',
  playbackMode: getDefaultPlaybackMode(),
  selectedListId: localStorage.getItem('selectedListId') || 'all',
  excludedCategories: readJsonStorage('excludedCategories', {
    live: [],
    movies: []
  }),
  selectedCategories: readJsonStorage('selectedCategories', {
    live: [],
    movies: []
  }),
  categories: {
    live: [],
    movies: []
  },
  curatedLists: [],
  kind: 'live',
  items: [],
  hasMore: false,
  isLoading: false,
  requestId: 0,
  selected: null,
  guideLoaded: {},
  guideLoading: {},
  videoFullscreen: false,
  playbackRequestId: 0,
  currentHlsSessionId: null,
  hls: null,
  remoteSequence: 0,
  remotePollTimer: null,
  setupId: null,
  setupTab: 'connection',
  setupPollTimer: null,
  remoteQrTrigger: null,
  editingListId: null,
  listEditorReturnFocus: null,
  editorItems: [],
  editorHasMore: false,
  editorIsLoading: false,
  editorRequestId: 0,
  editorSelectedIds: {}
};

var pageSize = 240;
var editorPageSize = 120;
var guidePageSize = 12;
var listSearchTimer = null;

var els = {
  serverUrl: document.getElementById('serverUrl'),
  playlistUrl: document.getElementById('playlistUrl'),
  epgUrl: document.getElementById('epgUrl'),
  liveMode: document.getElementById('liveMode'),
  moviesMode: document.getElementById('moviesMode'),
  search: document.getElementById('search'),
  curatedListSelect: document.getElementById('curatedListSelect'),
  categorySelect: document.getElementById('categorySelect'),
  newList: document.getElementById('newList'),
  openSetup: document.getElementById('openSetup'),
  closeSetup: document.getElementById('closeSetup'),
  newSession: document.getElementById('newSession'),
  saveSetup: document.getElementById('saveSetup'),
  startPhoneSetup: document.getElementById('startPhoneSetup'),
  showRemoteQrMain: document.getElementById('showRemoteQrMain'),
  closeRemoteQr: document.getElementById('closeRemoteQr'),
  remoteQrOverlay: document.getElementById('remoteQrOverlay'),
  remoteQr: document.getElementById('remoteQr'),
  remoteQrText: document.getElementById('remoteQrText'),
  setupScreen: document.getElementById('setupScreen'),
  setupTabButtons: document.querySelectorAll('.setup-tab'),
  setupPanels: document.querySelectorAll('.setup-tab-panel'),
  setupQr: document.getElementById('setupQr'),
  setupUrlText: document.getElementById('setupUrlText'),
  setupCategoryList: document.getElementById('setupCategoryList'),
  setupCategorySummary: document.getElementById('setupCategorySummary'),
  selectAllCategories: document.getElementById('selectAllCategories'),
  clearCategories: document.getElementById('clearCategories'),
  backToPlaylistSetup: document.getElementById('backToPlaylistSetup'),
  finishCategorySetup: document.getElementById('finishCategorySetup'),
  setupNewList: document.getElementById('setupNewList'),
  setupListRows: document.getElementById('setupListRows'),
  setupListSummary: document.getElementById('setupListSummary'),
  closeSetupFromLists: document.getElementById('closeSetupFromLists'),
  listEditorScreen: document.getElementById('listEditorScreen'),
  closeListEditor: document.getElementById('closeListEditor'),
  cancelList: document.getElementById('cancelList'),
  saveList: document.getElementById('saveList'),
  createList: document.getElementById('createList'),
  listEditorTitle: document.getElementById('listEditorTitle'),
  listName: document.getElementById('listName'),
  listSearch: document.getElementById('listSearch'),
  listSelectedCount: document.getElementById('listSelectedCount'),
  listFooterCount: document.getElementById('listFooterCount'),
  listEditorItems: document.getElementById('listEditorItems'),
  connectionSummary: document.getElementById('connectionSummary'),
  status: document.getElementById('status'),
  grid: document.getElementById('grid'),
  preview: document.querySelector('.preview'),
  player: document.getElementById('player'),
  play: document.getElementById('play'),
  selectedTitle: document.getElementById('selectedTitle'),
  selectedChannel: document.getElementById('selectedChannel'),
  selectedMeta: document.getElementById('selectedMeta'),
  playbackTab: document.getElementById('playbackTab')
};

els.serverUrl.value = state.serverUrl;
els.playlistUrl.value = state.playlistUrl;
els.epgUrl.value = state.epgUrl;
updateGuideTabs();

els.openSetup.addEventListener('click', function () {
  openSetupScreen();
});
els.closeSetup.addEventListener('click', function () {
  closeSetupScreen();
});
for (var setupTabIndex = 0; setupTabIndex < els.setupTabButtons.length; setupTabIndex += 1) {
  els.setupTabButtons[setupTabIndex].addEventListener('click', function (event) {
    setSetupTab(event.currentTarget.dataset.setupTab);
  });
}
els.newSession.addEventListener('click', function () {
  createNewSessionFromSetup();
});
els.saveSetup.addEventListener('click', function () {
  connect().then(function () {
    if (state.sessionId) {
      startCategorySetup();
    } else if (!state.playlistUrl) {
      closeSetupScreen();
    }
  });
});
els.startPhoneSetup.addEventListener('click', function () {
  startPhoneSetup();
});
els.showRemoteQrMain.addEventListener('click', function () {
  showRemoteQr(els.showRemoteQrMain);
});
els.closeRemoteQr.addEventListener('click', function () {
  hideRemoteQr();
});
els.liveMode.addEventListener('click', function () {
  setKind('live');
});
els.moviesMode.addEventListener('click', function () {
  setKind('movies');
});
els.newList.addEventListener('click', function () {
  openListEditor();
});
els.setupNewList.addEventListener('click', function () {
  openListEditor(null, els.setupNewList);
});
els.closeSetupFromLists.addEventListener('click', function () {
  closeSetupScreen();
});
els.setupListRows.addEventListener('click', function (event) {
  var action = event.target && event.target.dataset ? event.target.dataset.action : '';
  var listId = event.target && event.target.dataset ? event.target.dataset.listId : '';
  var list;

  if (!action || !listId) {
    return;
  }

  list = findCuratedList(listId);
  if (!list) {
    return;
  }

  if (action === 'edit') {
    openListEditor(list, event.target);
  } else if (action === 'delete') {
    deleteCuratedList(list, event.target);
  }
});
els.curatedListSelect.addEventListener('change', function () {
  selectCuratedList(els.curatedListSelect.value);
});
els.categorySelect.addEventListener('change', function () {
  loadMedia();
});
els.selectAllCategories.addEventListener('click', function () {
  setAllSetupCategories(true);
});
els.clearCategories.addEventListener('click', function () {
  setAllSetupCategories(false);
});
els.backToPlaylistSetup.addEventListener('click', function () {
  setSetupTab('connection');
});
els.finishCategorySetup.addEventListener('click', function () {
  saveCategorySetup();
});
els.closeListEditor.addEventListener('click', closeListEditor);
els.cancelList.addEventListener('click', closeListEditor);
els.saveList.addEventListener('click', saveCuratedList);
els.createList.addEventListener('click', saveCuratedList);
els.listSearch.addEventListener('input', function () {
  window.clearTimeout(listSearchTimer);
  listSearchTimer = window.setTimeout(loadEditorMedia, 220);
});
els.listEditorItems.addEventListener('scroll', function () {
  if (isNearEditorBottom()) {
    loadMoreEditorMedia();
  }
});
els.setupScreen.addEventListener('focusin', function (event) {
  if (event.target && event.target.tagName === 'INPUT') {
    els.setupScreen.classList.add('keyboard-open');
    window.setTimeout(function () {
      event.target.scrollIntoView({
        block: 'center',
        inline: 'nearest'
      });
    }, 120);
  }
});
els.setupScreen.addEventListener('focusout', function () {
  window.setTimeout(function () {
    if (!els.setupScreen.contains(document.activeElement) ||
        document.activeElement.tagName !== 'INPUT') {
      els.setupScreen.classList.remove('keyboard-open');
    }
  }, 80);
});
els.search.addEventListener('input', function () {
  loadMedia();
});
if (els.play) {
  els.play.addEventListener('click', function () {
    playSelected();
  });
}
els.player.addEventListener('dblclick', function () {
  toggleFullscreen();
});
els.player.addEventListener('webkitbeginfullscreen', function () {
  state.videoFullscreen = true;
});
els.player.addEventListener('webkitendfullscreen', function () {
  state.videoFullscreen = false;
});
els.grid.addEventListener('scroll', function () {
  if (isNearGridBottom()) {
    loadMoreMedia();
  }
});

document.addEventListener('keydown', function (event) {
  var focused = document.activeElement;
  if (isBackKey(event) && !els.remoteQrOverlay.hidden) {
    event.preventDefault();
    event.stopPropagation();
    hideRemoteQr();
    return;
  }

  if (isBackKey(event) && !els.listEditorScreen.hidden) {
    event.preventDefault();
    event.stopPropagation();
    closeListEditor();
    return;
  }

  if (isBackKey(event) && !els.setupScreen.hidden) {
    event.preventDefault();
    event.stopPropagation();
    closeSetupScreen();
    return;
  }

  if (isStopKey(event)) {
    event.preventDefault();
    event.stopPropagation();
    stopPlayback();
    return;
  }

  if (isBackKey(event) && isVideoFullscreen()) {
    event.preventDefault();
    event.stopPropagation();
    exitFullscreen();
    return;
  }

  if (event.key === 'Enter' && focused === els.player) {
    toggleFullscreen();
    return;
  }

  if (isGridNavigationKey(event.key) && (focused === els.grid || focused.classList.contains('card'))) {
    event.preventDefault();
    moveGridFocus(event.key);
    return;
  }

  if (event.key === 'Enter' && focused && focused.classList.contains('card')) {
    var item = state.items[Number(focused.dataset.index)];
    selectItem(item);
    playSelected();
  }
});

document.addEventListener('fullscreenchange', syncFullscreenState);
document.addEventListener('webkitfullscreenchange', syncFullscreenState);
document.addEventListener('MSFullscreenChange', syncFullscreenState);
document.addEventListener('backbutton', function (event) {
  if (!els.remoteQrOverlay.hidden) {
    event.preventDefault();
    hideRemoteQr();
  } else if (!els.listEditorScreen.hidden) {
    event.preventDefault();
    closeListEditor();
  } else if (!els.setupScreen.hidden) {
    event.preventDefault();
    closeSetupScreen();
  } else if (isVideoFullscreen()) {
    event.preventDefault();
    exitFullscreen();
  }
});

function connect() {
  state.serverUrl = els.serverUrl.value.trim().replace(/\/$/, '');
  state.playlistUrl = els.playlistUrl.value.trim();
  state.epgUrl = els.epgUrl.value.trim();
  updateConnectionSummary();
  localStorage.setItem('serverUrl', state.serverUrl);
  localStorage.setItem('playlistUrl', state.playlistUrl);
  localStorage.setItem('epgUrl', state.epgUrl);
  stopRemotePolling();
  setStatus('Connecting...');

  return fetchJson('/api/status')
    .then(function (status) {
      if (!state.playlistUrl) {
        setStatus('Connected to ' + (status.host || 'desktop app') + '. Legacy playlist mode.');
        startRemotePolling();
        return loadCuratedLists()
          .then(loadCategories)
          .then(loadMedia);
      }

      return createSession()
        .then(function () {
          setStatus('Session connected. Phone remote ready.');
          updateConnectionSummary();
          startRemotePolling();
          return loadCuratedLists()
            .then(loadCategories)
            .then(loadMedia);
        });
    })
    .catch(function (error) {
      setStatus('Could not connect: ' + error.message);
    });
}

function createSession() {
  return postJson('/api/sessions', {
    deviceName: 'LG TV',
    playlistUrl: state.playlistUrl,
    epgUrl: state.epgUrl || null,
    playbackMode: state.playbackMode,
    excludedCategories: state.excludedCategories.live || [],
    selectedCategories: state.selectedCategories.live || [],
    requestedSessionId: state.sessionId || null
  }).then(function (session) {
    state.sessionId = session.sessionId || session.SessionId;
    state.remoteUrl = session.remoteUrl || session.RemoteUrl || '';
    state.remoteSequence = 0;

    if (!state.sessionId) {
      throw new Error('Desktop did not return a session id.');
    }

    localStorage.setItem('sessionId', state.sessionId);
    localStorage.setItem('remoteUrl', state.remoteUrl);
    publishSetupSession();
  });
}

function createNewSessionFromSetup() {
  clearSavedSession();
  stopRemotePolling();
  stopSetupPolling();
  state.setupId = null;
  els.setupQr.removeAttribute('src');
  els.setupUrlText.textContent = 'New session ready. Save & connect, or scan a fresh phone setup link.';
  setStatus('New session will be created on the next save.');
  startPhoneSetup();
}

function clearSavedSession() {
  state.sessionId = null;
  state.remoteUrl = '';
  state.remoteSequence = 0;
  localStorage.removeItem('sessionId');
  localStorage.removeItem('remoteUrl');
  updateConnectionSummary();
}

function openSetupScreen(tabName) {
  els.setupScreen.hidden = false;
  setSetupTab(tabName || 'connection');
  startPhoneSetup();
  window.setTimeout(function () {
    var activeTab = document.querySelector('.setup-tab.active');
    if (activeTab) {
      activeTab.focus();
    } else {
      els.startPhoneSetup.focus();
    }
  }, 50);
}

function closeSetupScreen() {
  els.setupScreen.hidden = true;
}

function setSetupTab(tabName) {
  var safeTab = tabName || 'connection';
  var index;

  state.setupTab = safeTab;

  for (index = 0; index < els.setupTabButtons.length; index += 1) {
    els.setupTabButtons[index].classList.toggle('active', els.setupTabButtons[index].dataset.setupTab === safeTab);
  }

  for (index = 0; index < els.setupPanels.length; index += 1) {
    els.setupPanels[index].hidden = els.setupPanels[index].dataset.setupPanel !== safeTab;
  }

  if (safeTab === 'categories') {
    loadCategories().then(renderCategorySetup);
  } else if (safeTab === 'favourites') {
    loadCuratedLists().then(renderSetupLists);
  }
}

function showRemoteQr(trigger) {
  state.remoteQrTrigger = trigger || els.showRemoteQrMain;

  if (!state.remoteUrl) {
    setStatus('Connect first, then show the remote QR.');
    els.openSetup.focus();
    return;
  }

  els.remoteQr.src = state.serverUrl + '/api/qr.svg?value=' + encodeURIComponent(state.remoteUrl);
  els.remoteQrText.textContent = state.remoteUrl;
  els.remoteQrOverlay.hidden = false;
  window.setTimeout(function () {
    els.closeRemoteQr.focus();
  }, 50);
}

function hideRemoteQr() {
  var target = state.remoteQrTrigger || els.showRemoteQrMain;

  els.remoteQrOverlay.hidden = true;
  els.remoteQr.removeAttribute('src');
  state.remoteQrTrigger = null;
  if (target) {
    target.focus();
  }
}

function startPhoneSetup() {
  state.serverUrl = els.serverUrl.value.trim().replace(/\/$/, '');
  localStorage.setItem('serverUrl', state.serverUrl);
  stopSetupPolling();
  state.setupId = null;
  setStatus('Creating phone setup link...');
  els.setupQr.removeAttribute('src');
  els.setupUrlText.textContent = 'Creating setup link...';

  postJson('/api/setup-links', {
    deviceName: 'LG TV'
  })
    .then(function (link) {
      state.setupId = link.setupId || link.SetupId;
      var setupUrl = link.setupUrl || link.SetupUrl;
      var qrUrl = link.qrUrl || link.QrUrl;

      els.setupQr.src = qrUrl;
      els.setupUrlText.textContent = setupUrl;
      setStatus('Scan setup QR with your phone.');
      pollPhoneSetup();
    })
    .catch(function (error) {
      setStatus('Phone setup failed: ' + error.message);
      els.setupUrlText.textContent = 'Could not create setup link.';
    });
}

function pollPhoneSetup() {
  if (!state.setupId) {
    return;
  }

  fetchJson('/api/setup-links/' + encodeURIComponent(state.setupId) + '/configuration')
    .then(function (configuration) {
      if (configuration.submitted) {
        applyPhoneSetup(configuration);
        return;
      }

      state.setupPollTimer = window.setTimeout(function () {
        state.setupPollTimer = null;
        pollPhoneSetup();
      }, 2000);
    })
    .catch(function (error) {
      setStatus('Phone setup polling stopped: ' + error.message);
    });
}

function applyPhoneSetup(configuration) {
  var excludedCategories = configuration.excludedCategories !== undefined
    ? configuration.excludedCategories
    : configuration.ExcludedCategories;
  var selectedCategories = configuration.selectedCategories !== undefined
    ? configuration.selectedCategories
    : configuration.SelectedCategories;
  stopSetupPolling();
  state.playlistUrl = configuration.playlistUrl || configuration.PlaylistUrl || '';
  state.epgUrl = configuration.epgUrl || configuration.EpgUrl || '';
  state.kind = 'live';
  state.selectedListId = 'all';
  els.liveMode.classList.add('active');
  els.moviesMode.classList.remove('active');
  updateGuideTabs();
  els.playlistUrl.value = state.playlistUrl;
  els.epgUrl.value = state.epgUrl;
  els.search.value = '';
  els.categorySelect.value = '';
  localStorage.setItem('playlistUrl', state.playlistUrl);
  localStorage.setItem('epgUrl', state.epgUrl);
  localStorage.setItem('selectedListId', state.selectedListId);
  if (Array.isArray(excludedCategories)) {
    state.excludedCategories.live = excludedCategories;
    localStorage.setItem('excludedCategories', JSON.stringify(state.excludedCategories));
  }
  if (Array.isArray(selectedCategories)) {
    state.selectedCategories.live = selectedCategories;
    localStorage.setItem('selectedCategories', JSON.stringify(state.selectedCategories));
  }
  setStatus('Phone setup saved. Connecting...');
  connect().then(function () {
    if (state.sessionId && !Array.isArray(excludedCategories)) {
      startCategorySetup();
      return;
    }

    closeSetupScreen();
    setStatus('Phone setup applied. Loading guide...');
    loadCategories()
      .then(loadMedia)
      .catch(function (error) {
        setStatus('Guide refresh failed: ' + error.message);
      });
  });
}

function publishSetupSession() {
  if (!state.setupId || !state.sessionId || !state.remoteUrl) {
    return;
  }

  postJson('/api/setup-links/' + encodeURIComponent(state.setupId) + '/session', {
    sessionId: state.sessionId,
    remoteUrl: state.remoteUrl
  }).catch(function () {
  });
}

function stopSetupPolling() {
  if (state.setupPollTimer) {
    window.clearTimeout(state.setupPollTimer);
    state.setupPollTimer = null;
  }
}

function updateConnectionSummary() {
  if (state.sessionId) {
    els.connectionSummary.textContent = 'Session connected. Use Setup to change playlist.';
  } else if (state.playlistUrl) {
    els.connectionSummary.textContent = 'Playlist saved. Open Setup to change it.';
  } else {
    els.connectionSummary.textContent = 'Open Setup to add a playlist.';
  }
}

function setKind(kind) {
  state.kind = kind;
  els.liveMode.classList.toggle('active', kind === 'live');
  els.moviesMode.classList.toggle('active', kind === 'movies');
  updateGuideTabs();

  return loadCuratedLists()
    .then(function () {
      return loadCategories();
    })
    .then(function () {
      return loadMedia();
    });
}

function applyRemoteKind(kind) {
  var nextKind = kind === 'movies' ? 'movies' : 'live';

  if (state.kind === nextKind) {
    return;
  }

  state.kind = nextKind;
  els.liveMode.classList.toggle('active', nextKind === 'live');
  els.moviesMode.classList.toggle('active', nextKind === 'movies');
  updateGuideTabs();
}

function updateGuideTabs() {
  els.playbackTab.textContent = state.playbackMode === 'hls' ? 'HLS 1080P' : 'Direct';
}

function loadCategories() {
  if (!state.sessionId) {
    state.categories[state.kind] = [];
    renderCategoryDropdown();
    return Promise.resolve();
  }

  return fetchJson(apiPath('/categories?kind=' + encodeURIComponent(state.kind)))
    .then(function (data) {
      state.categories[state.kind] = data.categories || [];
      renderCategoryDropdown();
    })
    .catch(function () {
      state.categories[state.kind] = [];
      renderCategoryDropdown();
    });
}

function renderCategoryDropdown() {
  var categories = getKeptCategories();
  var selected = els.categorySelect.value;
  var option;
  var index;

  els.categorySelect.innerHTML = '';
  option = document.createElement('option');
  option.value = '';
  option.textContent = 'All kept categories';
  els.categorySelect.appendChild(option);

  for (index = 0; index < categories.length; index += 1) {
    option = document.createElement('option');
    option.value = categories[index];
    option.textContent = categories[index];
    els.categorySelect.appendChild(option);
  }

  els.categorySelect.value = categories.indexOf(selected) >= 0 ? selected : '';
}

function getKeptCategories() {
  var excluded = getExcludedCategorySet(state.kind);
  var selected = getSelectedCategorySet(state.kind);
  var hasSelected = (state.selectedCategories[state.kind] || []).length > 0;
  return (state.categories[state.kind] || []).filter(function (category) {
    return hasSelected
      ? !!selected[category.toLowerCase()]
      : !excluded[category.toLowerCase()];
  });
}

function getExcludedCategorySet(kind) {
  var set = {};
  var excluded = state.excludedCategories[kind] || [];
  var index;

  for (index = 0; index < excluded.length; index += 1) {
    set[String(excluded[index]).toLowerCase()] = true;
  }

  return set;
}

function getSelectedCategorySet(kind) {
  var set = {};
  var selected = state.selectedCategories[kind] || [];
  var index;

  for (index = 0; index < selected.length; index += 1) {
    set[String(selected[index]).toLowerCase()] = true;
  }

  return set;
}

function startCategorySetup() {
  return loadCategories().then(function () {
    renderCategorySetup();
    openSetupScreen('categories');
    els.setupCategoryList.focus();
  });
}

function renderCategorySetup() {
  var categories = state.categories[state.kind] || [];
  var excluded = getExcludedCategorySet(state.kind);
  var selected = getSelectedCategorySet(state.kind);
  var hasSelected = (state.selectedCategories[state.kind] || []).length > 0;
  var categoryRows = [];
  var kept = 0;
  var index;
  var isChecked;

  els.setupCategoryList.innerHTML = '';
  els.setupCategoryList.scrollTop = 0;

  if (categories.length === 0) {
    els.setupCategoryList.innerHTML = '<p class="curated-empty">No categories were returned by the playlist.</p>';
    els.setupCategorySummary.textContent = 'No categories found.';
    return;
  }

  for (index = 0; index < categories.length; index += 1) {
    isChecked = hasSelected
      ? !!selected[categories[index].toLowerCase()]
      : !excluded[categories[index].toLowerCase()];
    if (isChecked) {
      kept += 1;
    }
    categoryRows.push({
      name: categories[index],
      checked: isChecked
    });
  }

  categoryRows.sort(function (left, right) {
    if (left.checked !== right.checked) {
      return left.checked ? -1 : 1;
    }

    return left.name.localeCompare(right.name, undefined, {
      sensitivity: 'base'
    });
  });

  for (index = 0; index < categoryRows.length; index += 1) {
    appendSetupCategory(categoryRows[index].name, categoryRows[index].checked);
  }

  els.setupCategorySummary.textContent = kept + ' of ' + categories.length + ' categories selected.';
}

function appendSetupCategory(category, isChecked) {
  var label = document.createElement('label');
  var checkbox = document.createElement('input');
  var name = document.createElement('span');

  label.className = 'category-choice';
  label.tabIndex = 0;
  checkbox.type = 'checkbox';
  checkbox.checked = isChecked;
  checkbox.value = category;
  checkbox.addEventListener('change', updateSetupCategorySummary);
  name.textContent = category;

  label.addEventListener('keydown', function (event) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      checkbox.checked = !checkbox.checked;
      updateSetupCategorySummary();
    }
  });

  label.appendChild(checkbox);
  label.appendChild(name);
  els.setupCategoryList.appendChild(label);
}

function setAllSetupCategories(isChecked) {
  var inputs = els.setupCategoryList.querySelectorAll('input[type="checkbox"]');
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    inputs[index].checked = isChecked;
  }

  updateSetupCategorySummary();
}

function updateSetupCategorySummary() {
  var inputs = els.setupCategoryList.querySelectorAll('input[type="checkbox"]');
  var kept = 0;
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    if (inputs[index].checked) {
      kept += 1;
    }
  }

  els.setupCategorySummary.textContent = kept + ' of ' + inputs.length + ' categories selected.';
}

function saveCategorySetup() {
  var inputs = els.setupCategoryList.querySelectorAll('input[type="checkbox"]');
  var excluded = [];
  var selected = [];
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    if (inputs[index].checked) {
      selected.push(inputs[index].value);
    } else {
      excluded.push(inputs[index].value);
    }
  }

  state.excludedCategories[state.kind] = excluded;
  state.selectedCategories[state.kind] = selected;
  localStorage.setItem('excludedCategories', JSON.stringify(state.excludedCategories));
  localStorage.setItem('selectedCategories', JSON.stringify(state.selectedCategories));
  renderCategoryDropdown();
  closeSetupScreen();
  saveSessionExcludedCategories(state.kind, excluded, selected)
    .then(loadMedia)
    .catch(function () {
      loadMedia();
    });
}

function saveSessionExcludedCategories(kind, excludedCategories, selectedCategories) {
  if (!state.sessionId) {
    return Promise.resolve();
  }

  return postJson(apiPath('/excluded-categories'), {
    kind: kind,
    excludedCategories: excludedCategories || [],
    selectedCategories: selectedCategories || []
  });
}

function loadCuratedLists() {
  if (!state.sessionId) {
    state.curatedLists = [
      {
        id: 'all',
        name: state.kind === 'live' ? 'All channels' : 'All movies',
        count: null,
        builtIn: true
      }
    ];
    if (state.kind === 'live') {
      state.curatedLists.push({
        id: 'builtin-uk',
        name: 'UK Essentials',
        count: null,
        builtIn: true
      });
    }
    renderCuratedLists();
    return Promise.resolve();
  }

  return fetchJson(apiPath('/curated-lists?kind=' + encodeURIComponent(state.kind)))
    .then(function (data) {
      state.curatedLists = data.lists || [];
      if (!findCuratedList(state.selectedListId)) {
        state.selectedListId = 'all';
        localStorage.setItem('selectedListId', state.selectedListId);
      }
      renderCuratedLists();
    })
    .catch(function () {
      state.selectedListId = 'all';
      localStorage.setItem('selectedListId', state.selectedListId);
      state.curatedLists = getFallbackCuratedLists();
      renderCuratedLists();
    });
}

function getFallbackCuratedLists() {
  var lists = [
    {
      id: 'all',
      name: state.kind === 'live' ? 'All channels' : 'All movies',
      count: null,
      builtIn: true
    }
  ];

  if (state.kind === 'live') {
    lists.push({
      id: 'builtin-uk',
      name: 'UK Essentials',
      count: null,
      builtIn: true
    });
  }

  return lists;
}

function renderCuratedLists() {
  els.curatedListSelect.innerHTML = '';
  if (state.curatedLists.length === 0) {
    var emptyOption = document.createElement('option');
    emptyOption.value = 'all';
    emptyOption.textContent = 'All channels';
    els.curatedListSelect.appendChild(emptyOption);
    renderSetupLists();
    return;
  }

  for (var index = 0; index < state.curatedLists.length; index += 1) {
    appendCuratedListOption(state.curatedLists[index]);
  }

  els.curatedListSelect.value = findCuratedList(state.selectedListId) ? state.selectedListId : 'all';
  renderSetupLists();
}

function appendCuratedListOption(list) {
  var option = document.createElement('option');
  var count = typeof list.count === 'number' ? ' (' + list.count + ')' : '';

  option.value = list.id;
  option.textContent = list.name + count;
  els.curatedListSelect.appendChild(option);
}

function selectCuratedList(id) {
  state.selectedListId = id || 'all';
  localStorage.setItem('selectedListId', state.selectedListId);
  renderCuratedLists();
  loadMedia();
}

function findCuratedList(id) {
  for (var index = 0; index < state.curatedLists.length; index += 1) {
    if (state.curatedLists[index].id === id) {
      return state.curatedLists[index];
    }
  }

  return null;
}

function getSelectedListName() {
  var list = findCuratedList(state.selectedListId);
  return list ? list.name : state.kind === 'live' ? 'All channels' : 'All movies';
}

function renderSetupLists() {
  var customLists = [];
  var index;

  if (!els.setupListRows) {
    return;
  }

  els.setupListRows.innerHTML = '';

  if (!state.sessionId) {
    els.setupListRows.innerHTML = '<p class="curated-empty">Connect your playlist before creating favourites lists.</p>';
    els.setupListSummary.textContent = 'Connect first to manage lists.';
    return;
  }

  for (index = 0; index < state.curatedLists.length; index += 1) {
    if (!state.curatedLists[index].builtIn) {
      customLists.push(state.curatedLists[index]);
    }
  }

  if (customLists.length === 0) {
    els.setupListRows.innerHTML = '<p class="curated-empty">No favourites lists yet. Add a list to start curating channels.</p>';
    els.setupListSummary.textContent = 'No custom lists yet.';
    return;
  }

  for (index = 0; index < customLists.length; index += 1) {
    appendSetupListRow(customLists[index]);
  }

  els.setupListSummary.textContent = customLists.length + ' custom list' + (customLists.length === 1 ? '' : 's') + ' saved.';
}

function appendSetupListRow(list) {
  var row = document.createElement('article');
  var body = document.createElement('div');
  var title = document.createElement('strong');
  var meta = document.createElement('span');
  var actions = document.createElement('div');
  var edit = document.createElement('button');
  var remove = document.createElement('button');

  row.className = 'setup-list-row';
  body.className = 'setup-list-body';
  actions.className = 'setup-list-actions';
  title.textContent = list.name;
  meta.textContent = (typeof list.count === 'number' ? list.count : 0) + ' channel' + (list.count === 1 ? '' : 's');

  edit.className = 'mode';
  edit.type = 'button';
  edit.textContent = 'Edit';
  edit.dataset.action = 'edit';
  edit.dataset.listId = list.id;

  remove.className = 'mode danger-action';
  remove.type = 'button';
  remove.textContent = 'Delete';
  remove.dataset.action = 'delete';
  remove.dataset.listId = list.id;

  body.appendChild(title);
  body.appendChild(meta);
  actions.appendChild(edit);
  actions.appendChild(remove);
  row.appendChild(body);
  row.appendChild(actions);
  els.setupListRows.appendChild(row);
}

function openListEditor(list, returnFocus) {
  if (!state.sessionId) {
    setStatus('Connect first, then create lists.');
    els.openSetup.focus();
    return;
  }

  state.editorSelectedIds = {};
  state.editingListId = list && !list.builtIn ? list.id : null;
  state.listEditorReturnFocus = returnFocus || els.newList;
  state.editorItems = [];
  state.editorHasMore = false;
  state.editorRequestId += 1;
  els.listName.value = list && list.name ? list.name : '';
  els.listSearch.value = '';
  setEditorSelectedIds(list && list.itemIds);
  els.listEditorTitle.textContent = state.editingListId ? 'Edit favourites list' : 'New favourites list';
  els.createList.textContent = state.editingListId ? 'Save changes' : 'Create list';
  els.saveList.textContent = state.editingListId ? 'Save changes' : 'Save list';
  updateEditorSelectionCount();
  els.listEditorScreen.hidden = false;
  loadEditorMedia();
  window.setTimeout(function () {
    els.listName.focus();
  }, 50);
}

function closeListEditor() {
  var target = state.listEditorReturnFocus || els.newList;

  els.listEditorScreen.hidden = true;
  state.editingListId = null;
  state.listEditorReturnFocus = null;
  if (target && target.focus) {
    target.focus();
  }
}

function setEditorSelectedIds(ids) {
  var index;

  state.editorSelectedIds = {};
  if (!Array.isArray(ids)) {
    return;
  }

  for (index = 0; index < ids.length; index += 1) {
    if (ids[index]) {
      state.editorSelectedIds[String(ids[index])] = true;
    }
  }
}

function loadEditorMedia() {
  state.editorRequestId += 1;
  state.editorItems = [];
  state.editorHasMore = false;
  els.listEditorItems.innerHTML = '';
  els.listEditorItems.scrollTop = 0;
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
    })
    .catch(function (error) {
      if (requestId === state.editorRequestId) {
        els.listEditorItems.innerHTML = '<p class="curated-empty">Load failed: ' + error.message + '</p>';
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
    els.listEditorItems.innerHTML = '<p class="curated-empty">No channels found.</p>';
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
  var check = document.createElement('span');
  var image = item.icon ? document.createElement('img') : document.createElement('span');
  var body = document.createElement('span');
  var title = document.createElement('strong');
  var meta = document.createElement('span');
  var guide = document.createElement('span');

  button.className = 'editor-item';
  button.type = 'button';
  button.dataset.id = item.id;
  button.classList.toggle('selected', !!state.editorSelectedIds[item.id]);
  button.addEventListener('click', function () {
    toggleEditorItem(item.id, button);
  });

  check.className = 'editor-check';
  check.textContent = state.editorSelectedIds[item.id] ? 'x' : '';

  if (item.icon) {
    image.src = item.icon;
    image.alt = '';
  } else {
    image.className = 'editor-fallback';
    image.textContent = item.name.slice(0, 2).toUpperCase();
  }

  body.className = 'editor-item-body';
  title.textContent = item.name;
  meta.textContent = item.group || 'Ungrouped';
  var guideTitle = getProgrammeDisplayTitle(item, 'now');

  guide.textContent = guideTitle === 'Unknown' ? '' : guideTitle;
  body.appendChild(title);
  body.appendChild(meta);
  body.appendChild(guide);
  button.appendChild(check);
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
  button.querySelector('.editor-check').textContent = state.editorSelectedIds[id] ? 'x' : '';
  updateEditorSelectionCount();
}

function updateEditorSelectionCount() {
  var count = Object.keys(state.editorSelectedIds).length;
  els.listSelectedCount.textContent = count + ' selected';
  els.listFooterCount.textContent = count + ' channels selected';
}

function saveCuratedList() {
  var ids = Object.keys(state.editorSelectedIds);
  var name = els.listName.value.trim();
  if (!name) {
    setStatus('Name the list first.');
    els.listName.focus();
    return;
  }

  if (ids.length === 0) {
    setStatus('Select at least one channel.');
    return;
  }

  var request = {
    kind: state.kind,
    name: name,
    itemIds: ids
  };
  var saveRequest = state.editingListId
    ? putJson(apiPath('/curated-lists/' + encodeURIComponent(state.editingListId)), request)
    : postJson(apiPath('/curated-lists'), request);

  saveRequest
    .then(function (result) {
      var list = result.list || result.List;
      if (list && list.id) {
        state.selectedListId = list.id;
        localStorage.setItem('selectedListId', state.selectedListId);
      }
      closeListEditor();
      return loadCuratedLists();
    })
    .then(function () {
      setStatus('List saved.');
      return loadMedia();
    })
    .catch(function (error) {
      setStatus('Could not save list: ' + error.message);
    });
}

function deleteCuratedList(list, trigger) {
  if (!list || list.builtIn) {
    setStatus('Built-in lists cannot be deleted.');
    return;
  }

  setStatus('Deleting ' + list.name + '...');
  deleteJson(apiPath('/curated-lists/' + encodeURIComponent(list.id) + '?kind=' + encodeURIComponent(state.kind)))
    .then(function () {
      if (state.selectedListId === list.id) {
        state.selectedListId = 'all';
        localStorage.setItem('selectedListId', state.selectedListId);
      }

      return loadCuratedLists();
    })
    .then(function () {
      renderSetupLists();
      setStatus('List deleted.');
      return loadMedia();
    })
    .then(function () {
      if (trigger && trigger.focus) {
        trigger.focus();
      }
    })
    .catch(function (error) {
      setStatus('Could not delete list: ' + error.message);
    });
}

function isNearEditorBottom() {
  return els.listEditorItems.scrollTop + els.listEditorItems.clientHeight >= els.listEditorItems.scrollHeight - 180;
}

function loadMedia() {
  state.requestId += 1;
  state.items = [];
  state.hasMore = false;
  state.selected = null;
  state.guideLoaded = {};
  state.guideLoading = {};
  els.selectedTitle.textContent = 'Nothing selected';
  els.selectedChannel.textContent = 'No channel selected';
  els.selectedMeta.textContent = '';
  els.grid.scrollTop = 0;
  renderItems();

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
    group: els.categorySelect.value,
    skip: String(skip),
    limit: String(pageSize)
  });

  if (state.selectedListId && state.selectedListId !== 'all') {
    params.set('list', state.selectedListId);
  }

  state.isLoading = true;
  setStatus(skip > 0 ? 'Loading more...' : 'Loading...');

  return fetchJson(apiPath('/media?' + params.toString()))
    .then(function (data) {
      var label;
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

      label = state.kind === 'live' ? 'channels' : 'movies';
      if (state.hasMore) {
        setStatus(state.items.length + '+ ' + label + ' shown in ' + getSelectedListName() + '. Scroll for more.');
      } else {
        setStatus(state.items.length + ' ' + label + ' shown in ' + getSelectedListName() + '.');
      }

      loadGuideForItems(newItems, requestId);
    })
    .catch(function (error) {
      if (requestId === state.requestId) {
        if (state.selectedListId !== 'all') {
          state.selectedListId = 'all';
          localStorage.setItem('selectedListId', state.selectedListId);
          renderCuratedLists();
          setStatus('List failed, showing all channels.');
          return loadMedia();
        }

        setStatus('Load failed: ' + error.message);
      }
    })
    .then(function () {
      if (requestId === state.requestId) {
        state.isLoading = false;
      }
    });
}

function renderItems() {
  els.grid.innerHTML = '';
  appendItems(state.items, 0);
}

function appendItems(items, startIndex) {
  for (var index = 0; index < items.length; index += 1) {
    appendCard(items[index], startIndex + index);
  }
}

function isNearGridBottom() {
  return els.grid.scrollTop + els.grid.clientHeight >= els.grid.scrollHeight - 240;
}

function isGridNavigationKey(key) {
  return key === 'ArrowDown' ||
         key === 'ArrowUp' ||
         key === 'ArrowLeft' ||
         key === 'ArrowRight' ||
         key === 'PageDown' ||
         key === 'PageUp';
}

function moveGridFocus(key) {
  var cards = els.grid.querySelectorAll('.card');
  var current = document.activeElement && document.activeElement.classList.contains('card')
    ? Number(document.activeElement.dataset.index)
    : -1;
  var columns = getGridColumnCount();
  var next = current < 0 ? 0 : current;

  if (key === 'ArrowRight') {
    next += 1;
  } else if (key === 'ArrowLeft') {
    next -= 1;
  } else if (key === 'ArrowDown') {
    next += columns;
  } else if (key === 'ArrowUp') {
    next -= columns;
  } else if (key === 'PageDown') {
    next += columns * 3;
  } else if (key === 'PageUp') {
    next -= columns * 3;
  }

  next = Math.max(0, Math.min(cards.length - 1, next));
  if (cards[next]) {
    cards[next].focus();
    selectItem(state.items[next]);
    if (next >= state.items.length - getGridColumnCount() * 2) {
      loadMoreMedia();
    }
  }
}

function getGridColumnCount() {
  var columns = window.getComputedStyle(els.grid).gridTemplateColumns.split(' ');
  return Math.max(1, columns.length || 3);
}

function appendCard(item, index) {
  var card = document.createElement('div');
  var image = item.icon ? document.createElement('img') : document.createElement('div');
  var channel = document.createElement('div');
  var channelText = document.createElement('div');
  var now = document.createElement('div');
  var next = document.createElement('div');
  var mode = document.createElement('div');
  var title;
  var group;

  card.className = 'card';
  card.setAttribute('role', 'button');
  card.tabIndex = 0;
  card.dataset.index = String(index);
  card.dataset.id = item.id;
  card.addEventListener('click', function () {
    selectItem(item);
    playSelected();
  });
  card.addEventListener('focus', function () {
    card.scrollIntoView({
      block: 'nearest',
      inline: 'nearest'
    });
  });

  if (item.icon) {
    image.src = item.icon;
    image.alt = '';
  } else {
    image.className = 'fallback';
    image.textContent = item.name.slice(0, 2).toUpperCase();
  }

  channel.className = 'channel-cell';
  channelText.className = 'channel-text';
  channelText.innerHTML = '<strong></strong><span></span>';
  title = channelText.querySelector('strong');
  group = channelText.querySelector('span');
  title.textContent = item.name;
  group.textContent = item.group || 'Ungrouped';
  channel.appendChild(image);
  channel.appendChild(channelText);

  now.className = 'programme-cell';
  next.className = 'programme-cell';
  mode.className = state.playbackMode === 'hls' ? 'mode-cell hls-format' : 'mode-cell direct-format';
  setProgrammeCell(now, item, 'now');
  setProgrammeCell(next, item, 'next');
  mode.textContent = state.playbackMode === 'hls' ? 'HLS 1080P' : 'Direct';

  card.appendChild(channel);
  card.appendChild(now);
  card.appendChild(next);
  card.appendChild(mode);
  els.grid.appendChild(card);
}

function loadGuideForItems(items, requestId) {
  var ids = [];
  var index;
  var id;

  if (state.kind !== 'live') {
    return;
  }

  for (index = 0; index < items.length; index += 1) {
    id = items[index].id;
    if (id &&
        id.indexOf('live-') === 0 &&
        !state.guideLoaded[id] &&
        !state.guideLoading[id]) {
      state.guideLoading[id] = true;
      ids.push(id);
    }
  }

  if (ids.length > 0) {
    requestGuideChunk(ids, 0, requestId);
  }
}

function requestGuideChunk(ids, offset, requestId) {
  var chunk;

  if (requestId !== state.requestId || offset >= ids.length) {
    return;
  }

  chunk = ids.slice(offset, offset + guidePageSize);
  fetchJson(apiPath('/guide?ids=' + encodeURIComponent(chunk.join(','))))
    .then(function (data) {
      var guide = data.guide || {};
      var index;
      var id;
      var item;
      var info;

      if (requestId !== state.requestId) {
        return;
      }

      for (index = 0; index < chunk.length; index += 1) {
        id = chunk[index];
        item = findItemById(id);
        info = guide[id];

        state.guideLoaded[id] = true;
        state.guideLoading[id] = false;

        if (item && info) {
          item.nowTitle = info.nowTitle || info.NowTitle || item.nowTitle;
          item.nextTitle = info.nextTitle || info.NextTitle || item.nextTitle;
          item.nowDescription = info.nowDescription || info.NowDescription || item.nowDescription;
          item.nowStart = info.nowStart || info.NowStart || item.nowStart;
          item.nowEnd = info.nowEnd || info.NowEnd || item.nowEnd;
          item.nextDescription = info.nextDescription || info.NextDescription || item.nextDescription;
          item.nextStart = info.nextStart || info.NextStart || item.nextStart;
          item.nextEnd = info.nextEnd || info.NextEnd || item.nextEnd;
        }

        if (item) {
          updateGuideCells(item);
        }
      }
    })
    .catch(function () {
      var index;

      for (index = 0; index < chunk.length; index += 1) {
        state.guideLoading[chunk[index]] = false;
      }
    })
    .then(function () {
      requestGuideChunk(ids, offset + guidePageSize, requestId);
    });
}

function findItemById(id) {
  var index;

  for (index = 0; index < state.items.length; index += 1) {
    if (state.items[index].id === id) {
      return state.items[index];
    }
  }

  return null;
}

function updateGuideCells(item) {
  var cards = els.grid.querySelectorAll('.card');
  var index;
  var cells;

  for (index = 0; index < cards.length; index += 1) {
    if (cards[index].dataset.id === item.id) {
      cells = cards[index].querySelectorAll('.programme-cell');
      if (cells[0]) {
        setProgrammeCell(cells[0], item, 'now');
      }
      if (cells[1]) {
        setProgrammeCell(cells[1], item, 'next');
      }
    }
  }

  if (state.selected && state.selected.id === item.id) {
    refreshSelectedDetails(item);
  }
}

function selectItem(item) {
  state.selected = item;
  refreshSelectedDetails(item);
}

function refreshSelectedDetails(item) {
  var nowTitle = getProgrammeDisplayTitle(item, 'now');
  var nextTitle = getProgrammeDisplayTitle(item, 'next');
  var nowTime = getProgrammeTime(item, 'now');
  var nextTime = getProgrammeTime(item, 'next');
  var description = truncateText(getProgrammeDescription(item, 'now'), 260);
  var group = item.group || 'Ungrouped';
  var meta = '';

  els.selectedChannel.textContent = item.name + ' • ' + group;
  els.selectedTitle.textContent = nowTitle === 'Unknown' ? item.name : nowTitle;
  if (nowTime) {
    meta += nowTime + '\n';
  }
  meta += description || 'No programme description is available.';
  if (nextTitle !== 'Unknown' && nextTitle !== 'pending...') {
    meta += '\nNext: ' + nextTitle + (nextTime ? '  ' + nextTime : '');
  }
  els.selectedMeta.textContent = meta;
}

function setProgrammeCell(cell, item, slot) {
  var title = document.createElement('span');
  var time = document.createElement('small');
  var programmeTime = getProgrammeTime(item, slot);

  cell.innerHTML = '';
  title.className = 'programme-title';
  title.textContent = getProgrammeDisplayTitle(item, slot);
  cell.appendChild(title);

  if (programmeTime) {
    time.className = 'programme-time';
    time.textContent = programmeTime;
    cell.appendChild(time);
  }
}

function getProgrammeTitle(item, slot) {
  var value = slot === 'now'
    ? item.now || item.nowTitle || item.current || item.currentTitle
    : item.next || item.nextTitle;

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

function getProgrammeDescription(item, slot) {
  var value = slot === 'now'
    ? item.nowDescription || item.description || (item.now && item.now.description)
    : item.nextDescription || (item.next && item.next.description);

  return value || '';
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

function playSelected(options) {
  var playbackRequestId;
  var shouldFullscreen = options && options.fullscreen;

  if (!state.selected) {
    setStatus('Select something first.');
    return;
  }

  playbackRequestId = ++state.playbackRequestId;
  beginPlaybackLoading();
  if (state.playbackMode === 'direct') {
    startDirectPlayback(state.selected.url)
      .then(function () {
        if (playbackRequestId !== state.playbackRequestId) {
          return;
        }

        setPreviewLoading(false);
        if (shouldFullscreen) {
          enterFullscreen();
        }
        setStatus('Playing direct stream.');
      })
      .catch(function (error) {
        if (playbackRequestId !== state.playbackRequestId) {
          return;
        }

        setPreviewLoading(false);
        setStatus('Direct playback failed: ' + error.message);
      });
    return;
  }

  fetchJson(apiPath('/play/' + encodeURIComponent(state.selected.id) + '?kind=' + encodeURIComponent(state.kind)))
      .then(function (playback) {
        if (playbackRequestId !== state.playbackRequestId) {
          stopDesktopHlsSessionById(playback.sessionId || parseHlsSessionId(playback.url));
          return Promise.reject(new Error('stale playback request'));
        }

        state.currentHlsSessionId = playback.sessionId || parseHlsSessionId(playback.url);
        return startHlsPlayback(playback.url);
      })
      .then(function () {
        if (playbackRequestId !== state.playbackRequestId) {
          return;
        }

        setPreviewLoading(false);
        if (shouldFullscreen) {
          enterFullscreen();
        }
        setStatus('Playing through desktop HLS proxy.');
      })
      .catch(function (error) {
        if (playbackRequestId !== state.playbackRequestId || error.message === 'stale playback request') {
          return;
        }

        setPreviewLoading(false);
        setStatus('HLS playback failed: ' + error.message);
      });
}

function beginPlaybackLoading() {
  stopHlsPlayback();
  stopDesktopHlsSession();
  resetVideo();
  setPreviewLoading(true);
  setStatus('Loading stream...');
}

function setPreviewLoading(isLoading) {
  if (els.preview) {
    els.preview.classList.toggle('loading', isLoading);
  }
}

function startDirectPlayback(url) {
  stopHlsPlayback();
  state.currentHlsSessionId = null;
  resetVideo();
  els.player.src = url;
  els.player.load();
  return els.player.play();
}

function startHlsPlayback(url) {
  stopHlsPlayback();
  resetVideo();

  if (els.player.canPlayType('application/vnd.apple.mpegurl')) {
    els.player.src = url;
    els.player.load();
    return els.player.play();
  }

  if (window.Hls && window.Hls.isSupported()) {
    return new Promise(function (resolve, reject) {
      var started = false;

      state.hls = new window.Hls({
        lowLatencyMode: true,
        backBufferLength: 30
      });

      state.hls.on(window.Hls.Events.MANIFEST_PARSED, function () {
        started = true;
        els.player.play().then(resolve).catch(reject);
      });

      state.hls.on(window.Hls.Events.ERROR, function (eventName, data) {
        if (data && data.fatal) {
          stopHlsPlayback();
          reject(new Error(data.details || data.type || 'HLS playback error'));
        }
      });

      state.hls.loadSource(url);
      state.hls.attachMedia(els.player);

      window.setTimeout(function () {
        if (!started) {
          reject(new Error('HLS manifest did not start in time.'));
        }
      }, 20000);
    });
  }

  return Promise.reject(new Error('This webOS runtime does not support native HLS or Media Source playback.'));
}

function resetVideo() {
  els.player.pause();
  els.player.removeAttribute('src');
  els.player.load();
}

function stopHlsPlayback() {
  if (state.hls) {
    state.hls.destroy();
    state.hls = null;
  }
}

function stopPlayback() {
  state.playbackRequestId += 1;
  stopHlsPlayback();
  stopDesktopHlsSession();
  resetVideo();
  if (isVideoFullscreen()) {
    exitFullscreen();
  }
  setPreviewLoading(false);
  setStatus('Playback stopped.');
}

function startRemotePolling() {
  if (state.remotePollTimer) {
    return;
  }

  if (!state.sessionId && state.playlistUrl) {
    return;
  }

  fetchJson(apiPath('/remote/commands?after=999999999999'))
    .then(function (data) {
      if (data.sequence || data.Sequence) {
        state.remoteSequence = data.sequence || data.Sequence;
      }
    })
    .catch(function () {
    })
    .then(function () {
      pollRemoteCommands();
    });
}

function stopRemotePolling() {
  if (state.remotePollTimer) {
    window.clearTimeout(state.remotePollTimer);
    state.remotePollTimer = null;
  }

  state.remoteSequence = 0;
}

function pollRemoteCommands() {
  fetchJson(apiPath('/remote/commands?after=' + encodeURIComponent(state.remoteSequence)))
    .then(function (data) {
      var command = data.command || data.Command;

      if (data.sequence || data.Sequence) {
        state.remoteSequence = Math.max(state.remoteSequence, data.sequence || data.Sequence);
      }

      if (data.hasCommand && command) {
        handleRemoteCommand(command);
      }
    })
    .catch(function () {
    })
    .then(function () {
      state.remotePollTimer = window.setTimeout(function () {
        state.remotePollTimer = null;
        pollRemoteCommands();
      }, 1000);
    });
}

function handleRemoteCommand(command) {
  var type = command.type || command.Type;
  var kind = command.kind || command.Kind || state.kind;
  var item = normalizeRemoteItem(command.item || command.Item);
  var itemId = command.itemId || command.ItemId;

  if (type === 'stop') {
    stopPlayback();
    setStatus('Stopped from phone remote.');
    return;
  }

  if (type !== 'play') {
    return;
  }

  applyRemoteKind(kind);
  updateGuideTabs();

  if (item) {
    selectItem(item);
    setStatus('Phone remote selected ' + item.name + '.');
    playSelected({ fullscreen: true });
    return;
  }

  if (itemId) {
    fetchJson(apiPath('/item/' + encodeURIComponent(itemId) + '?kind=' + encodeURIComponent(state.kind)))
      .then(function (fetchedItem) {
        selectItem(fetchedItem);
        setStatus('Phone remote selected ' + fetchedItem.name + '.');
        playSelected({ fullscreen: true });
      })
      .catch(function (error) {
        setStatus('Phone command failed: ' + error.message);
      });
  }
}

function normalizeRemoteItem(item) {
  if (!item) {
    return null;
  }

  return {
    id: item.id || item.Id,
    kind: item.kind || item.Kind,
    name: item.name || item.Name || 'Remote channel',
    group: item.group || item.Group || 'Ungrouped',
    url: item.url || item.Url,
    icon: item.icon || item.Icon || null,
    epgId: item.epgId || item.EpgId || null,
    nowTitle: item.nowTitle || item.NowTitle,
    nextTitle: item.nextTitle || item.NextTitle,
    nowDescription: item.nowDescription || item.NowDescription,
    nextDescription: item.nextDescription || item.NextDescription,
    nowStart: item.nowStart || item.NowStart,
    nowEnd: item.nowEnd || item.NowEnd,
    nextStart: item.nextStart || item.NextStart,
    nextEnd: item.nextEnd || item.NextEnd
  };
}

function stopDesktopHlsSession() {
  var sessionId = state.currentHlsSessionId;

  state.currentHlsSessionId = null;
  stopDesktopHlsSessionById(sessionId);
}

function stopDesktopHlsSessionById(sessionId) {
  if (state.sessionId) {
    postJson(apiPath('/stop'), {}).catch(function () {
    });
  } else if (sessionId) {
    fetchJson('/api/stop/' + encodeURIComponent(sessionId)).catch(function () {
    });
  }
}

function parseHlsSessionId(url) {
  var match = String(url || '').match(/\/api\/(?:sessions\/[^/]+\/)?hls\/([^/]+)\//);
  return match ? decodeURIComponent(match[1]) : null;
}

function toggleFullscreen() {
  if (isVideoFullscreen()) {
    exitFullscreen();
  } else {
    enterFullscreen();
  }
}

function enterFullscreen() {
  var player = els.player;
  var request = player.requestFullscreen ||
    player.webkitRequestFullscreen ||
    player.webkitEnterFullscreen ||
    player.msRequestFullscreen;

  if (els.preview) {
    els.preview.classList.add('fullscreen-preview');
  }

  if (request) {
    var fullscreenResult;

    state.videoFullscreen = true;
    try {
      fullscreenResult = request.call(player);
      if (fullscreenResult && fullscreenResult.catch) {
        fullscreenResult.catch(function () {
          state.videoFullscreen = true;
        });
      }
    } catch (error) {
      setStatus('Showing full-screen player.');
    }
  } else {
    state.videoFullscreen = true;
  }
}

function exitFullscreen() {
  var player = els.player;
  var exitDocument = document.exitFullscreen ||
    document.webkitExitFullscreen ||
    document.msExitFullscreen;

  try {
    if (player.webkitDisplayingFullscreen && player.webkitExitFullscreen) {
      player.webkitExitFullscreen();
    } else if (exitDocument) {
      exitDocument.call(document);
    } else if (player.webkitExitFullscreen) {
      player.webkitExitFullscreen();
    }
  } catch (error) {
    setStatus('Returned to guide preview.');
  }

  if (els.preview) {
    els.preview.classList.remove('fullscreen-preview');
  }

  state.videoFullscreen = false;
  player.focus();
}

function isVideoFullscreen() {
  return state.videoFullscreen ||
    document.fullscreenElement === els.player ||
    document.webkitFullscreenElement === els.player ||
    document.msFullscreenElement === els.player ||
    els.player.webkitDisplayingFullscreen;
}

function syncFullscreenState() {
  var fullscreenElement = document.fullscreenElement ||
    document.webkitFullscreenElement ||
    document.msFullscreenElement;

  state.videoFullscreen = fullscreenElement === els.player ||
    !!els.player.webkitDisplayingFullscreen;
}

function isBackKey(event) {
  return event.key === 'Escape' ||
    event.key === 'Back' ||
    event.key === 'BrowserBack' ||
    event.key === 'Backspace' && isVideoFullscreen() ||
    event.keyCode === 461 ||
    event.keyCode === 10009;
}

function isStopKey(event) {
  return event.key === 'MediaStop' ||
    event.key === 'Stop' ||
    event.keyCode === 413;
}

function fetchJson(path) {
  return fetch(state.serverUrl + path)
    .then(function (response) {
      if (!response.ok) {
        return response.text().then(function (body) {
          throw new Error(path + ' returned ' + response.status + ' ' + response.statusText + formatErrorBody(body));
        });
      }

      return response.json();
    })
    .catch(function (error) {
      if (error.message && error.message.indexOf(path) !== -1) {
        throw error;
      }

      throw new Error(path + ' failed: ' + error.message);
    });
}

function postJson(path, body) {
  return sendJson(path, 'POST', body);
}

function putJson(path, body) {
  return sendJson(path, 'PUT', body);
}

function deleteJson(path) {
  return sendJson(path, 'DELETE', null);
}

function sendJson(path, method, body) {
  var options = {
    method: method,
    headers: {
      'Content-Type': 'application/json'
    }
  };

  if (body !== null && body !== undefined) {
    options.body = JSON.stringify(body || {});
  }

  return fetch(state.serverUrl + path, {
    method: options.method,
    headers: options.headers,
    body: options.body
  })
    .then(function (response) {
      if (!response.ok) {
        return response.text().then(function (responseBody) {
          throw new Error(path + ' returned ' + response.status + ' ' + response.statusText + formatErrorBody(responseBody));
        });
      }

      return response.json();
    })
    .catch(function (error) {
      if (error.message && error.message.indexOf(path) !== -1) {
        throw error;
      }

      throw new Error(path + ' failed: ' + error.message);
    });
}

function apiPath(path) {
  if (state.sessionId) {
    return '/api/sessions/' + encodeURIComponent(state.sessionId) + path;
  }

  return '/api' + path;
}

function setStatus(message) {
  els.status.textContent = message;
}

function formatErrorBody(body) {
  if (!body) {
    return '';
  }

  try {
    var parsed = JSON.parse(body);
    return parsed.detail ? ' - ' + parsed.detail : ' - ' + body.slice(0, 120);
  } catch (error) {
    return ' - ' + body.slice(0, 120);
  }
}

updateConnectionSummary();
if (state.playlistUrl) {
  connect();
} else {
  openSetupScreen();
  setStatus('Open Setup to add your playlist.');
}
