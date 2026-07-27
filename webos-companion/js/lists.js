// IPTV Sidekick - favourites list management

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
