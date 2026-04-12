const CACHE_NAME = "gaming-dashboard-studio-v3";

self.addEventListener("install", event => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener("activate", event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(key => key !== CACHE_NAME).map(key => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("message", event => {
  if (event.data?.type !== "cache-assets" || !Array.isArray(event.data.urls)) {
    return;
  }

  event.waitUntil((async () => {
    const cache = await caches.open(CACHE_NAME);
    for (const url of event.data.urls) {
      if (!url) {
        continue;
      }

      try {
        await cache.add(url);
      } catch {
      }
    }
  })());
});

self.addEventListener("fetch", event => {
  const { request } = event;
  if (request.method !== "GET") {
    return;
  }

  const url = new URL(request.url);
  const isMediaAsset = url.pathname.startsWith("/api/media/local/")
    || url.pathname.startsWith("/api/media/pexels/stream");

  if (!isMediaAsset) {
    return;
  }

  event.respondWith((async () => {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(request);
    if (cached) {
      return cached;
    }

    const response = await fetch(request);
    if (response.ok) {
      cache.put(request, response.clone());
    }

    return response;
  })());
});
