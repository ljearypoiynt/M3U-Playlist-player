// IPTV Sidekick - API and status helpers

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

function loadAppVersion() {
  if (!els.appVersion || !window.fetch) {
    return;
  }

  fetch('appinfo.json')
    .then(function (response) {
      if (!response.ok) {
        throw new Error('appinfo.json returned ' + response.status);
      }

      return response.json();
    })
    .then(function (appInfo) {
      if (appInfo && appInfo.version) {
        els.appVersion.textContent = 'v' + appInfo.version;
      }
    })
    .catch(function () {
      // Keep the version baked into index.html if appinfo.json is unavailable.
    });
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
