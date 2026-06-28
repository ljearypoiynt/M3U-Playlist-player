var setupCode = new URLSearchParams(window.location.search).get('code') || '';
var remoteWaitTimer = null;
var previewCategories = [];

var els = {
  statusPill: document.getElementById('statusPill'),
  deviceName: document.getElementById('deviceName'),
  detailsStep: document.getElementById('detailsStep'),
  categoryStep: document.getElementById('categoryStep'),
  playlistUrl: document.getElementById('playlistUrl'),
  epgUrl: document.getElementById('epgUrl'),
  continueToCategories: document.getElementById('continueToCategories'),
  categoryCount: document.getElementById('categoryCount'),
  categoryHelp: document.getElementById('categoryHelp'),
  categoryList: document.getElementById('categoryList'),
  selectAllCategories: document.getElementById('selectAllCategories'),
  clearCategories: document.getElementById('clearCategories'),
  backToDetails: document.getElementById('backToDetails'),
  save: document.getElementById('save'),
  resultTitle: document.getElementById('resultTitle'),
  resultText: document.getElementById('resultText')
};

els.continueToCategories.addEventListener('click', function () {
  loadCategoryPreview();
});

els.selectAllCategories.addEventListener('click', function () {
  setAllCategories(true);
});

els.clearCategories.addEventListener('click', function () {
  setAllCategories(false);
});

els.backToDetails.addEventListener('click', function () {
  showDetailsStep();
});

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
        showDetailsStep();
      }
    })
    .catch(function (error) {
      showError('Setup expired', error.message);
    });
}

function getSetupDetails() {
  var playlistUrl = els.playlistUrl.value.trim();
  var epgUrl = els.epgUrl.value.trim();

  if (!playlistUrl) {
    showError('Playlist needed', 'Paste the playlist URL from your provider email.');
    els.playlistUrl.focus();
    return null;
  }

  return {
    playlistUrl: playlistUrl,
    epgUrl: epgUrl || null
  };
}

function loadCategoryPreview() {
  var details = getSetupDetails();

  if (!details) {
    return;
  }

  setStatus('Loading', false);
  els.continueToCategories.disabled = true;
  els.resultTitle.textContent = 'Loading categories';
  els.resultText.textContent = 'Checking the playlist and getting the category list.';
  fetch('/api/setup-links/' + encodeURIComponent(setupCode) + '/category-preview', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      playlistUrl: details.playlistUrl,
      epgUrl: details.epgUrl,
      kind: 'live'
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
    .then(function (data) {
      previewCategories = data.categories || [];
      renderCategories();
      showCategoryStep();
      setStatus('Choose', false);
      els.resultTitle.textContent = 'Choose categories';
      els.resultText.textContent = 'Deselect anything you do not want on the TV, then save.';
    })
    .catch(function (error) {
      showError('Could not load categories', error.message);
    })
    .finally(function () {
      els.continueToCategories.disabled = false;
    });
}

function saveSetup() {
  var details = getSetupDetails();
  var excludedCategories;
  var selectedCategories;

  if (!details) {
    return;
  }

  excludedCategories = getExcludedCategories();
  selectedCategories = getSelectedCategories();

  setStatus('Saving', false);
  els.save.disabled = true;
  fetch('/api/setup-links/' + encodeURIComponent(setupCode), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      playlistUrl: details.playlistUrl,
      epgUrl: details.epgUrl,
      excludedCategories: excludedCategories,
      selectedCategories: selectedCategories
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
    })
    .finally(function () {
      els.save.disabled = false;
    });
}

function renderCategories() {
  var index;

  els.categoryList.innerHTML = '';

  if (previewCategories.length === 0) {
    els.categoryList.innerHTML = '<p class="empty small-empty">No categories were returned by this playlist.</p>';
    els.categoryCount.textContent = '0 selected';
    return;
  }

  for (index = 0; index < previewCategories.length; index += 1) {
    appendCategoryChoice(previewCategories[index], true);
  }

  updateCategoryCount();
}

function appendCategoryChoice(category, isChecked) {
  var label = document.createElement('label');
  var checkbox = document.createElement('input');
  var name = document.createElement('span');

  label.className = 'category-choice';
  checkbox.type = 'checkbox';
  checkbox.checked = isChecked;
  checkbox.value = category;
  checkbox.addEventListener('change', updateCategoryCount);
  name.textContent = category;

  label.appendChild(checkbox);
  label.appendChild(name);
  els.categoryList.appendChild(label);
}

function setAllCategories(isChecked) {
  var inputs = els.categoryList.querySelectorAll('input[type="checkbox"]');
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    inputs[index].checked = isChecked;
  }

  updateCategoryCount();
}

function getExcludedCategories() {
  var inputs = els.categoryList.querySelectorAll('input[type="checkbox"]');
  var excluded = [];
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    if (!inputs[index].checked) {
      excluded.push(inputs[index].value);
    }
  }

  return excluded;
}

function getSelectedCategories() {
  var inputs = els.categoryList.querySelectorAll('input[type="checkbox"]');
  var selected = [];
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    if (inputs[index].checked) {
      selected.push(inputs[index].value);
    }
  }

  return selected;
}

function updateCategoryCount() {
  var inputs = els.categoryList.querySelectorAll('input[type="checkbox"]');
  var kept = 0;
  var index;

  for (index = 0; index < inputs.length; index += 1) {
    if (inputs[index].checked) {
      kept += 1;
    }
  }

  els.categoryCount.textContent = kept + ' selected';
}

function showDetailsStep() {
  els.detailsStep.hidden = false;
  els.categoryStep.hidden = true;
}

function showCategoryStep() {
  els.detailsStep.hidden = true;
  els.categoryStep.hidden = false;
  if (els.categoryList.firstElementChild) {
    els.categoryList.firstElementChild.scrollIntoView({ block: 'nearest' });
  }
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
