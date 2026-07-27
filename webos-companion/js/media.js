// IPTV Sidekick - media grid, navigation, and guide rendering

function loadMedia() {
  state.requestId += 1;
  state.items = [];
  state.hasMore = false;
  state.selected = null;
  state.guideLoaded = {};
  state.guideLoading = {};
  state.guideRetryQueued = {};
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

function isSidebarControl(target) {
  return !!target &&
    els.sidebar.contains(target) &&
    typeof target.focus === 'function' &&
    !target.disabled;
}

function isFocusInSidebar(target) {
  return !!target && els.sidebar.contains(target);
}

function focusGridFromSidebar() {
  var card = findGridCardForSelectedItem() || els.grid.querySelector('.card');

  if (card) {
    card.focus();
    selectItem(state.items[Number(card.dataset.index)]);
    return;
  }

  els.grid.focus();
}

function focusLastSidebarControl() {
  var target = isUsableSidebarControl(state.lastSidebarControl)
    ? state.lastSidebarControl
    : getFirstUsableSidebarControl();

  if (target) {
    target.focus();
  }
}

function findGridCardForSelectedItem() {
  var cards = els.grid.querySelectorAll('.card');
  var index;

  if (!state.selected) {
    return null;
  }

  for (index = 0; index < cards.length; index += 1) {
    if (cards[index].dataset.id === state.selected.id) {
      return cards[index];
    }
  }

  return null;
}

function getFirstUsableSidebarControl() {
  var controls = els.sidebar.querySelectorAll('button, input, select, [tabindex]');
  var index;

  for (index = 0; index < controls.length; index += 1) {
    if (isUsableSidebarControl(controls[index])) {
      return controls[index];
    }
  }

  return null;
}

function isUsableSidebarControl(control) {
  return !!control &&
    els.sidebar.contains(control) &&
    typeof control.focus === 'function' &&
    !control.disabled &&
    !control.hidden &&
    control.getAttribute('aria-hidden') !== 'true' &&
    control.offsetParent !== null;
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
  card.addEventListener('dblclick', function (event) {
    event.preventDefault();
    selectItem(item);
    playSelected({ fullscreen: true });
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
    requestGuideChunk(ids, 0, requestId, 'main');
  }
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
      var guideLoading = !!(data.guideLoading || data.GuideLoading);
      var missingLookup = {};
      var shortIds = [];
      var index;
      var id;
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
        id = chunk[index];
        item = findItemById(id);
        info = guide[id];
        isMissing = missingLookup[id] || !info || isMissingGuideInfo(info);

        if (guideSource === 'main' && isMissing && guideLoading) {
          state.guideLoading[id] = false;
        } else if (guideSource === 'main' && isMissing) {
          shortIds.push(id);
        } else {
          state.guideLoaded[id] = true;
          state.guideLoading[id] = false;
        }

        if (item && info && !isMissing) {
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

      if (guideSource === 'main' && guideLoading) {
        scheduleGuideRetry(chunk, requestId);
      } else if (guideSource === 'main' && shortIds.length > 0) {
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

function scheduleGuideRetry(ids, requestId) {
  var retryIds = [];
  var index;
  var id;

  for (index = 0; index < ids.length; index += 1) {
    id = ids[index];
    if (!state.guideLoaded[id] && !state.guideRetryQueued[id]) {
      state.guideRetryQueued[id] = true;
      retryIds.push(id);
    }
  }

  if (retryIds.length === 0) {
    return;
  }

  window.setTimeout(function () {
    var pending = [];
    var retryIndex;
    var retryId;

    if (requestId !== state.requestId) {
      return;
    }

    for (retryIndex = 0; retryIndex < retryIds.length; retryIndex += 1) {
      retryId = retryIds[retryIndex];
      state.guideRetryQueued[retryId] = false;
      if (!state.guideLoaded[retryId] && !state.guideLoading[retryId]) {
        state.guideLoading[retryId] = true;
        pending.push(retryId);
      }
    }

    if (pending.length > 0) {
      requestGuideChunk(pending, 0, requestId, 'main');
    }
  }, 12000);
}

function isMissingGuideInfo(info) {
  if (!info) {
    return true;
  }

  return !(info.nowTitle || info.NowTitle || info.nextTitle || info.NextTitle);
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
