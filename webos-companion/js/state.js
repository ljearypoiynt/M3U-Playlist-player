// IPTV Sidekick - shared state and DOM references

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

var gatewayUrl = 'https://api.iptvsidekick.live';
localStorage.removeItem('serverUrl');

var state = {
  serverUrl: gatewayUrl,
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
  guideRetryQueued: {},
  lastSidebarControl: null,
  videoFullscreen: false,
  nativeFullscreen: false,
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
  sidebar: document.querySelector('.sidebar'),
  setupScreen: document.getElementById('setupScreen'),
  setupTabButtons: document.querySelectorAll('.setup-tab'),
  setupPanels: document.querySelectorAll('.setup-tab-panel'),
  setupQr: document.getElementById('setupQr'),
  setupUrlText: document.getElementById('setupUrlText'),
  setupCategoryList: document.getElementById('setupCategoryList'),
  setupCategorySummary: document.getElementById('setupCategorySummary'),
  setupProgress: document.getElementById('setupProgress'),
  setupProgressText: document.getElementById('setupProgressText'),
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
  createList: document.getElementById('createList'),
  listEditorTitle: document.getElementById('listEditorTitle'),
  listName: document.getElementById('listName'),
  listSearch: document.getElementById('listSearch'),
  listSelectedCount: document.getElementById('listSelectedCount'),
  listFooterCount: document.getElementById('listFooterCount'),
  listEditorItems: document.getElementById('listEditorItems'),
  connectionSummary: document.getElementById('connectionSummary'),
  appVersion: document.getElementById('appVersion'),
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

