const CACHE_NAME = "gaming-dashboard-studio-v4";

async function broadcastStatus(type, statuses) {
  const clients = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
  for (const client of clients) {
    client.postMessage({ type, statuses });
  }
}

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
  if (!Array.isArray(event.data?.urls)) {
    return;
  }

  event.waitUntil((async () => {
    const cache = await caches.open(CACHE_NAME);
    const urls = [...new Set(event.data.urls.filter(Boolean))];
    const statuses = {};

    if (event.data.type === "cache-status") {
      for (const url of urls) {
        const cached = await cache.match(url);
        statuses[url] = cached ? "cached" : "idle";
      }
      await broadcastStatus("cache-status", statuses);
      return;
    }

    if (event.data.type !== "cache-assets") {
      return;
    }

    for (const url of urls) {
      statuses[url] = "pending";
    }
    await broadcastStatus("cache-assets-complete", statuses);

    for (const url of urls) {
      try {
        const response = await fetch(url, { cache: "no-store" });
        if (!response.ok) {
          statuses[url] = "error";
          continue;
        }

        await cache.put(url, response.clone());
        statuses[url] = "cached";
      } catch {
        statuses[url] = "error";
      }
    }

    await broadcastStatus("cache-assets-complete", statuses);
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
