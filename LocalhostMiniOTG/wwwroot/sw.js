const CACHE_NAME = 'miniOTG-music-v1';
const APP_SHELL = ['/Music', '/manifest.json', '/icon-192.svg'];

// Install: cache app shell
self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => cache.addAll(APP_SHELL))
            .then(() => self.skipWaiting())
    );
});

// Activate: clean old caches
self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))
        ).then(() => self.clients.claim())
    );
});

// Fetch: cache audio streams for offline playback
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);

    // Cache audio files from /api/stream (music files)
    if (url.pathname === '/api/stream' && event.request.method === 'GET') {
        event.respondWith(
            caches.open(CACHE_NAME).then(async cache => {
                // Try cache first
                const cached = await cache.match(event.request);
                if (cached) return cached;

                // Fetch from network and cache for offline
                try {
                    const response = await fetch(event.request);
                    const contentType = response.headers.get('content-type') || '';
                    // Only cache audio files (not video/photo)
                    if (contentType.startsWith('audio/') || contentType === 'application/octet-stream') {
                        // Clone before caching since response can only be consumed once
                        cache.put(event.request, response.clone());
                    }
                    return response;
                } catch {
                    return new Response('Offline - track not cached', { status: 503 });
                }
            })
        );
        return;
    }

    // For page navigation, try network first, fall back to cache
    if (event.request.mode === 'navigate') {
        event.respondWith(
            fetch(event.request).catch(() => caches.match('/Music'))
        );
        return;
    }

    // Everything else: network first, cache fallback
    event.respondWith(
        fetch(event.request).catch(() => caches.match(event.request))
    );
});
