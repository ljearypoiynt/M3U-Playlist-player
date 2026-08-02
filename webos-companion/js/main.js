// IPTV Sidekick - event binding and startup

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
  setSetupBusy(true, 'Connecting to your playlist...');
  connect().then(function (connected) {
    if (connected && state.sessionId) {
      startCategorySetup();
    } else if (connected && !state.playlistUrl) {
      closeSetupScreen();
    }
    setSetupBusy(false);
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
function shouldUseSetupKeyboardLayout(target) {
  if (!target || target.tagName !== 'INPUT') {
    return false;
  }

  return ['email', 'number', 'password', 'search', 'tel', 'text', 'url'].indexOf(target.type) !== -1;
}

els.setupScreen.addEventListener('focusin', function (event) {
  if (shouldUseSetupKeyboardLayout(event.target)) {
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
        !shouldUseSetupKeyboardLayout(document.activeElement)) {
      els.setupScreen.classList.remove('keyboard-open');
    }
  }, 80);
});
els.search.addEventListener('input', function () {
  window.clearTimeout(mediaSearchTimer);
  mediaSearchTimer = window.setTimeout(loadMedia, 360);
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
  state.nativeFullscreen = true;
  setFullscreenUi(true);
});
els.player.addEventListener('webkitendfullscreen', function () {
  state.nativeFullscreen = false;
  setFullscreenUi(false);
});
els.grid.addEventListener('scroll', function () {
  if (isNearGridBottom()) {
    loadMoreMedia();
  }
});
els.sidebar.addEventListener('focusin', function (event) {
  if (isSidebarControl(event.target)) {
    state.lastSidebarControl = event.target;
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

  if (event.key === 'ArrowRight' && isFocusInSidebar(focused)) {
    event.preventDefault();
    focusGridFromSidebar();
    return;
  }

  if (event.key === 'ArrowLeft' && (focused === els.grid || focused.classList.contains('card'))) {
    event.preventDefault();
    focusLastSidebarControl();
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


// Start after every feature script has registered its functions.
loadAppVersion();
updateConnectionSummary();
if (state.playlistUrl) {
  connect();
} else {
  openSetupScreen();
  setStatus('Open Setup to add your playlist.');
}
