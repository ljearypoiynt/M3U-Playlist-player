var CACHE_NAME = 'm3u-remote-v4';
var CORE_ASSETS = [
  '/remote',
  '/remote/styles.css',
  '/remote/app.js',
  '/remote/iptv-logo-large.png'
];

self.addEventListener('install', function (event) {
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then(function (cache) {
        return cache.addAll(CORE_ASSETS);
      })
      .then(function () {
        return self.skipWaiting();
      })
  );
});

self.addEventListener('activate', function (event) {
  event.waitUntil(
    caches.keys()
      .then(function (keys) {
        return Promise.all(keys.map(function (key) {
          return key === CACHE_NAME ? Promise.resolve() : caches.delete(key);
        }));
      })
      .then(function () {
        return self.clients.claim();
      })
  );
});

self.addEventListener('fetch', function (event) {
  if (event.request.method !== 'GET') {
    return;
  }

  event.respondWith(
    fetch(event.request)
      .then(function (response) {
        if (response && response.ok && event.request.url.indexOf('/api/') === -1) {
          var copy = response.clone();
          caches.open(CACHE_NAME).then(function (cache) {
            cache.put(event.request, copy);
          });
        }

        return response;
      })
      .catch(function () {
        return caches.match(event.request);
      })
  );
});
