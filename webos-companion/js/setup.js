// IPTV Sidekick - setup, session, QR, and category flows

function setSetupBusy(isBusy, message) {
  if (els.setupProgress) {
    els.setupProgress.hidden = !isBusy;
  }

  if (els.setupProgressText && message) {
    els.setupProgressText.textContent = message;
  }

  els.saveSetup.disabled = !!isBusy;
  els.newSession.disabled = !!isBusy;
  els.startPhoneSetup.disabled = !!isBusy;
  els.setupScreen.classList.toggle('is-processing', !!isBusy);
}

function connect() {
  state.serverUrl = gatewayUrl;
  state.playlistUrl = els.playlistUrl.value.trim();
  state.epgUrl = els.epgUrl.value.trim();
  updateConnectionSummary();
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
          .then(loadMedia)
          .then(function () {
            return true;
          });
      }

      return createSession()
        .then(function () {
          setStatus('Session connected. Phone remote ready.');
          updateConnectionSummary();
          startRemotePolling();
          return loadCuratedLists()
            .then(loadCategories)
            .then(loadMedia)
            .then(function () {
              return true;
            });
        });
    })
    .catch(function (error) {
      setStatus('Could not connect: ' + error.message);
      return false;
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
  state.serverUrl = gatewayUrl;
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
  connect().then(function (connected) {
    if (!connected) {
      return;
    }

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
