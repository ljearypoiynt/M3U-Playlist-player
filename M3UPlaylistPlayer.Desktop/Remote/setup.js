var setupCode = new URLSearchParams(window.location.search).get('code') || '';
var remoteWaitTimer = null;

var els = {
  statusPill: document.getElementById('statusPill'),
  deviceName: document.getElementById('deviceName'),
  playlistUrl: document.getElementById('playlistUrl'),
  epgUrl: document.getElementById('epgUrl'),
  save: document.getElementById('save'),
  resultTitle: document.getElementById('resultTitle'),
  resultText: document.getElementById('resultText')
};

els.save.addEventListener('click', function () {
  saveSetup();
});

function loadSetupLink() {
  if (!setupCode) {
    showError('No setup code', 'Open this page from the QR code shown on the TV.');
    return;
  }

  fetchJson('/api/setup-links/' + encodeURIComponent(setupCode))
    .then(function (link) {
      els.deviceName.textContent = 'Setting up ' + (link.deviceName || 'LG TV') + '.';
      if (link.submitted) {
        setStatus('Saved', false);
        if (link.remoteReady && link.remoteUrl) {
          redirectToRemote(link.remoteUrl);
        } else {
          els.resultTitle.textContent = 'Already saved';
          els.resultText.textContent = 'Waiting for the TV to create the remote session.';
          waitForRemoteUrl();
        }
      } else {
        setStatus('Ready', false);
      }
    })
    .catch(function (error) {
      showError('Setup expired', error.message);
    });
}

function saveSetup() {
  var playlistUrl = els.playlistUrl.value.trim();
  var epgUrl = els.epgUrl.value.trim();

  if (!playlistUrl) {
    showError('Playlist needed', 'Paste the playlist URL from your provider email.');
    els.playlistUrl.focus();
    return;
  }

  setStatus('Saving', false);
  fetch('/api/setup-links/' + encodeURIComponent(setupCode), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      playlistUrl: playlistUrl,
      epgUrl: epgUrl || null
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
    .then(function () {
      setStatus('Saved', false);
      els.resultTitle.textContent = 'Saved to TV';
      els.resultText.textContent = 'Waiting for the TV to create your remote session.';
      waitForRemoteUrl();
    })
    .catch(function (error) {
      showError('Could not save', error.message);
    });
}

function waitForRemoteUrl() {
  if (remoteWaitTimer) {
    window.clearTimeout(remoteWaitTimer);
    remoteWaitTimer = null;
  }

  fetchJson('/api/setup-links/' + encodeURIComponent(setupCode))
    .then(function (link) {
      if (link.remoteReady && link.remoteUrl) {
        redirectToRemote(link.remoteUrl);
        return;
      }

      remoteWaitTimer = window.setTimeout(waitForRemoteUrl, 1500);
    })
    .catch(function (error) {
      showError('Waiting stopped', error.message);
    });
}

function redirectToRemote(remoteUrl) {
  setStatus('Opening', false);
  els.resultTitle.textContent = 'Opening remote';
  els.resultText.textContent = 'Taking you to the phone remote for this TV.';
  window.setTimeout(function () {
    window.location.href = remoteUrl;
  }, 700);
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

function showError(title, message) {
  setStatus('Error', true);
  els.resultTitle.textContent = title;
  els.resultText.textContent = message;
}

function setStatus(message, isError) {
  els.statusPill.textContent = message;
  els.statusPill.classList.toggle('error', !!isError);
}

function formatError(status, body) {
  if (!body) {
    return 'Request failed ' + status;
  }

  try {
    var parsed = JSON.parse(body);
    return parsed.error || parsed.detail || 'Request failed ' + status;
  } catch (error) {
    return body.slice(0, 120);
  }
}

loadSetupLink();
