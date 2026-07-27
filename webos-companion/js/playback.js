// IPTV Sidekick - playback, remote commands, and fullscreen handling

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

  setFullscreenUi(true);

  if (request) {
    var fullscreenResult;

    try {
      fullscreenResult = request.call(player);
      if (fullscreenResult && fullscreenResult.catch) {
        fullscreenResult.catch(function () {
          setFullscreenUi(true);
        });
      }
    } catch (error) {
      setStatus('Showing full-screen player.');
    }
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

  setFullscreenUi(false);
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
  var isNativeFullscreen = fullscreenElement === els.player ||
    !!els.player.webkitDisplayingFullscreen;

  if (isNativeFullscreen) {
    state.nativeFullscreen = true;
    setFullscreenUi(true);
  } else if (state.nativeFullscreen) {
    state.nativeFullscreen = false;
    setFullscreenUi(false);
  }
}

function setFullscreenUi(isFullscreen) {
  state.videoFullscreen = !!isFullscreen;
  document.body.classList.toggle('video-fullscreen', !!isFullscreen);

  if (els.preview) {
    els.preview.classList.toggle('fullscreen-preview', !!isFullscreen);
  }
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
