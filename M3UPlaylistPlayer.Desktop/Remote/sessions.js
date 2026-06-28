var els = {
  statusPill: document.getElementById('statusPill'),
  resultCount: document.getElementById('resultCount'),
  sessions: document.getElementById('sessions'),
  refresh: document.getElementById('refresh')
};

els.refresh.addEventListener('click', loadSessions);

function loadSessions() {
  setStatus('Loading', false);
  return fetchJson('/api/sessions')
    .then(function (data) {
      var sessions = data.sessions || [];
      els.resultCount.textContent = sessions.length + ' active session' + (sessions.length === 1 ? '' : 's');
      renderSessions(sessions);
      setStatus('Updated', false);
    })
    .catch(function (error) {
      els.resultCount.textContent = 'Unable to load sessions';
      els.sessions.innerHTML = '<p class="empty">' + escapeHtml(error.message) + '</p>';
      setStatus('Error', true);
    });
}

function renderSessions(sessions) {
  if (sessions.length === 0) {
    els.sessions.innerHTML = '<p class="empty">No active TV sessions yet.</p>';
    return;
  }

  els.sessions.innerHTML = '';
  sessions.forEach(function (session) {
    var article = document.createElement('article');
    var remoteUrl = window.location.origin + '/remote?session=' + encodeURIComponent(session.sessionId);
    article.className = 'session-card';
    article.innerHTML =
      '<div>' +
        '<p class="eyebrow">' + escapeHtml(session.sourceHost || 'Unknown source') + '</p>' +
        '<h2>' + escapeHtml(session.deviceName || 'TV session') + '</h2>' +
        '<p>' + escapeHtml(formatCounts(session)) + '</p>' +
      '</div>' +
      '<dl>' +
        '<div><dt>Mode</dt><dd>' + escapeHtml(session.playbackMode || 'hls') + '</dd></div>' +
        '<div><dt>Last seen</dt><dd>' + escapeHtml(formatDate(session.lastSeenAt)) + '</dd></div>' +
        '<div><dt>Expires</dt><dd>' + escapeHtml(formatDate(session.expiresAt)) + '</dd></div>' +
      '</dl>' +
      '<a href="' + remoteUrl + '">' + escapeHtml(remoteUrl) + '</a>';
    els.sessions.appendChild(article);
  });
}

function formatCounts(session) {
  var counts = [];
  if (session.liveCount !== null && session.liveCount !== undefined) {
    counts.push(session.liveCount + ' live shown');
  }
  if (session.movieCount !== null && session.movieCount !== undefined) {
    counts.push(session.movieCount + ' movies shown');
  }

  return counts.length > 0 ? counts.join(' - ') : 'Playlist connected';
}

function formatDate(value) {
  if (!value) {
    return 'Unknown';
  }

  return new Date(value).toLocaleString();
}

function fetchJson(path) {
  return fetch(path)
    .then(function (response) {
      if (!response.ok) {
        throw new Error(path + ' returned ' + response.status);
      }

      return response.json();
    });
}

function setStatus(message, isError) {
  els.statusPill.textContent = message;
  els.statusPill.classList.toggle('error', !!isError);
}

function escapeHtml(value) {
  return String(value || '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

loadSessions();
window.setInterval(loadSessions, 15000);
