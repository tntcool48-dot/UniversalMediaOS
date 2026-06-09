using System;
using System.IO;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services
{
    public class ConsumetBootstrapper
    {
        private readonly string _servicesDir;
        private readonly string _consumetDir;

        public ConsumetBootstrapper(string baseDirectory)
        {
            _servicesDir = Path.Combine(baseDirectory, "services");
            _consumetDir = Path.Combine(_servicesDir, "consumet");
            Directory.CreateDirectory(_servicesDir);
        }

        public async Task<bool> EnsureLatestConsumetAsync()
        {
            try
            {
                if (!Directory.Exists(_consumetDir)) Directory.CreateDirectory(_consumetDir);
                string serverJsPath = Path.Combine(_consumetDir, "index.js");

                // Always regenerate the server file to pick up scraper updates.
                Console.WriteLine("Generating GogoAnime scraper microservice...");
                string serverCode = GetServerCode();
                await File.WriteAllTextAsync(serverJsPath, serverCode);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error bootstrapping scraper server: {ex.Message}");
                return false;
            }
        }

        private string GetServerCode()
        {
            return @"
'use strict';
const http = require('http');
const https = require('https');
const url = require('url');
const querystring = require('querystring');
const crypto = require('crypto');

// ─── Configuration ───────────────────────────────────────────
const PORT = 3000;
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';
const BASE_URL = 'https://anitaku.pe';
const AJAX_URL = 'https://ajax.gogocdn.net';
const AJAX_SEARCH_URL = AJAX_URL + '/site/loadAjaxSearch';

// ─── Helpers ─────────────────────────────────────────────────

/** Fetch a URL and return { statusCode, headers, body } */
function fetch(targetUrl, opts = {}) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(targetUrl);
    const options = {
      hostname: parsed.hostname,
      port: parsed.port || 443,
      path: parsed.pathname + parsed.search,
      method: opts.method || 'GET',
      headers: {
        'User-Agent': UA,
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
        'Accept-Language': 'en-US,en;q=0.5',
        ...(opts.headers || {})
      },
      timeout: 15000
    };

    const transport = parsed.protocol === 'https:' ? https : http;
    const req = transport.request(options, (res) => {
      // Follow redirects (301, 302, 307, 308)
      if ([301, 302, 307, 308].includes(res.statusCode) && res.headers.location) {
        let redirectUrl = res.headers.location;
        if (redirectUrl.startsWith('/')) {
          redirectUrl = parsed.protocol + '//' + parsed.hostname + redirectUrl;
        }
        return fetch(redirectUrl, opts).then(resolve).catch(reject);
      }
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        resolve({
          statusCode: res.statusCode,
          headers: res.headers,
          body: Buffer.concat(chunks).toString('utf-8')
        });
      });
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('Request timed out')); });
    req.end();
  });
}

/** Decode common HTML entities */
function decodeEntities(str) {
  return str
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '""')
    .replace(/&#039;/g, ""'"")
    .replace(/&#39;/g, ""'"")
    .replace(/&apos;/g, ""'"");
}

/** Simple regex-based HTML attribute extractor */
function extractAttr(html, tag, attr) {
  const re = new RegExp('<' + tag + '[^>]*?' + attr + '=[""' + ""'"" + ']([^""' + ""'"" + ']*)[""' + ""'"" + ']', 'gi');
  const matches = [];
  let m;
  while ((m = re.exec(html)) !== null) matches.push(m[1]);
  return matches;
}

/** Send a JSON response */
function jsonResponse(res, statusCode, data) {
  res.writeHead(statusCode, {
    'Content-Type': 'application/json',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type'
  });
  res.end(JSON.stringify(data));
}

// ─── Route: Search ───────────────────────────────────────────
async function handleSearch(query, res) {
  try {
    // Try AJAX search endpoint first
    const searchUrl = AJAX_SEARCH_URL + '?keyword=' + encodeURIComponent(query) + '&id=-1';
    let resp;
    try {
      resp = await fetch(searchUrl, {
        headers: { 'X-Requested-With': 'XMLHttpRequest' }
      });
    } catch (e) {
      console.error('AJAX fetch failed, trying fallback:', e.message);
      return await handleSearchFallback(query, res);
    }

    if (resp.statusCode !== 200) {
      // Fallback: scrape the search page directly
      return await handleSearchFallback(query, res);
    }

    const results = [];
    const body = resp.body;

    // The AJAX response contains JSON with 'content' field holding HTML
    let html = body;
    try {
      const json = JSON.parse(body);
      html = json.content || '';
    } catch (e) {
      // body might be raw HTML
    }

    // Parse <a> tags from the search results
    // Pattern: <a href=""/category/..."" title=""..."">
    const linkRe = /<a\s+href=[""']([^""']*\/category\/[^""']*)[""'][^>]*>/gi;
    const titleRe = /title=[""']([^""']*)[""']/i;
    const imgRe = /<img[^>]*src=[""']([^""']*)[""']/gi;

    // Split by list items for better parsing
    const items = html.split(/<li/gi).slice(1);

    for (const item of items) {
      const linkMatch = /<a\s+href=[""']([^""']*)[""']/i.exec(item);
      const titleMatch = titleRe.exec(item);
      const imgMatch = /<img[^>]*src=[""']([^""']*)[""']/i.exec(item);

      if (linkMatch) {
        const href = linkMatch[1];
        const id = href.replace(/.*\/category\//, '').replace(/\/$/, '');
        const title = titleMatch ? decodeEntities(titleMatch[1]) : id.replace(/-/g, ' ');
        const image = imgMatch ? imgMatch[1] : '';

        if (id) {
          results.push({
            id: id,
            title: title,
            url: BASE_URL + '/category/' + id,
            image: image
          });
        }
      }
    }

    jsonResponse(res, 200, { results });
  } catch (err) {
    console.error('Search error:', err.message);
    jsonResponse(res, 503, { error: 'upstream unavailable', details: err.message });
  }
}

async function handleSearchFallback(query, res) {
  try {
    const searchPageUrl = BASE_URL + '/search.html?keyword=' + encodeURIComponent(query);
    const resp = await fetch(searchPageUrl);

    if (resp.statusCode !== 200) {
      return jsonResponse(res, 503, { error: 'upstream unavailable' });
    }

    const html = resp.body;
    const results = [];

    // Parse items from the search results page
    // Each result is in a <li> inside <ul class=""items"">
    const itemsSection = html.split(/class=[""']items[""']/i)[1] || '';
    const items = itemsSection.split(/<li/gi).slice(1);

    for (const item of items) {
      const linkMatch = /<a\s+href=[""']([^""']*\/category\/[^""']*)[""']/i.exec(item);
      const titleMatch = /title=[""']([^""']*)[""']/i.exec(item);
      const imgMatch = /<img[^>]*src=[""']([^""']*)[""']/i.exec(item);

      if (linkMatch) {
        const href = linkMatch[1];
        const id = href.replace(/.*\/category\//, '').replace(/\/$/, '');
        const title = titleMatch ? decodeEntities(titleMatch[1]) : id.replace(/-/g, ' ');
        const image = imgMatch ? imgMatch[1] : '';

        results.push({
          id: id,
          title: title,
          url: BASE_URL + '/category/' + id,
          image: image
        });
      }
    }

    jsonResponse(res, 200, { results });
  } catch (err) {
    console.error('Search fallback error:', err.message);
    jsonResponse(res, 503, { error: 'upstream unavailable', details: err.message });
  }
}

// ─── Route: Anime Info / Episode List ────────────────────────
async function handleInfo(animeId, res) {
  try {
    const pageUrl = BASE_URL + '/category/' + animeId;
    const resp = await fetch(pageUrl);

    if (resp.statusCode !== 200) {
      return jsonResponse(res, 503, { error: 'upstream unavailable' });
    }

    const html = resp.body;

    // Extract anime title
    const titleMatch = /<h1>([^<]*)<\/h1>/i.exec(html);
    const title = titleMatch ? decodeEntities(titleMatch[1].trim()) : animeId.replace(/-/g, ' ');

    // Extract movie_id and total episodes from the page
    const movieIdMatch = /class=[""']movie_id[""']\s+value=[""'](\d+)[""']/i.exec(html)
      || /input[^>]*id=[""']movie_id[""'][^>]*value=[""'](\d+)[""']/i.exec(html);
    const lastEpMatch = /class=[""']active[""']\s+ep_start=[""']\d+[""']\s+ep_end=[""'](\d+)[""']/i.exec(html)
      || /ep_end=[""'](\d+)[""']/gi;

    // Get alias/default_ep
    const aliasMatch = /class=[""']alias_anime[""']\s+value=[""']([^""']*)[""']/i.exec(html)
      || /input[^>]*id=[""']alias_anime[""'][^>]*value=[""']([^""']*)[""']/i.exec(html);
    const alias = aliasMatch ? aliasMatch[1] : '';

    let lastEp = 0;
    // Find all ep_end values and take the maximum
    const epEndRe = /ep_end=[""'](\d+)[""']/gi;
    let epMatch;
    while ((epMatch = epEndRe.exec(html)) !== null) {
      const n = parseInt(epMatch[1], 10);
      if (n > lastEp) lastEp = n;
    }

    const movieId = movieIdMatch ? movieIdMatch[1] : '';

    // Fetch episode list via AJAX
    let episodes = [];
    if (movieId && lastEp > 0) {
      const epListUrl = AJAX_URL + '/ajax/load-list-episode?ep_start=0&ep_end=' + lastEp + '&id=' + movieId + '&default_ep=0&alias=' + encodeURIComponent(alias);
      const epResp = await fetch(epListUrl, {
        headers: { 'X-Requested-With': 'XMLHttpRequest' }
      });

      if (epResp.statusCode === 200) {
        const epHtml = epResp.body;
        const epItems = epHtml.split(/<li/gi).slice(1);

        for (const item of epItems) {
          const hrefMatch = /<a\s+href=[""']\s*([^""'\s]*)\s*[""']/i.exec(item);
          const epNumMatch = /class=[""']name[""'][^>]*>.*?(\d+(?:\.\d+)?)/i.exec(item)
            || /EP\s*(\d+(?:\.\d+)?)/i.exec(item);

          if (hrefMatch) {
            const href = hrefMatch[1].trim();
            const epId = href.replace(/^\//, '').replace(/\/$/, '');
            const epNum = epNumMatch ? parseFloat(epNumMatch[1]) : 0;

            episodes.push({
              id: epId,
              number: epNum,
              url: BASE_URL + '/' + epId
            });
          }
        }

        // Sort episodes by number
        episodes.sort((a, b) => a.number - b.number);
      }
    }

    // If AJAX failed, try to build episode list from naming convention
    if (episodes.length === 0 && lastEp > 0) {
      for (let i = 1; i <= lastEp; i++) {
        const epId = animeId + '-episode-' + i;
        episodes.push({
          id: epId,
          number: i,
          url: BASE_URL + '/' + epId
        });
      }
    }

    jsonResponse(res, 200, {
      id: animeId,
      title: title,
      totalEpisodes: episodes.length,
      episodes: episodes
    });
  } catch (err) {
    console.error('Info error:', err.message);
    jsonResponse(res, 503, { error: 'upstream unavailable', details: err.message });
  }
}

// ─── Route: Watch / Streaming Sources ────────────────────────

// GogoCDN/Gogoplay AES keys (well-known, used by many open-source tools)
const GOGOCDN_KEYS = {
  key: Buffer.from('37911490979715163134003223491201'),
  secondKey: Buffer.from('54674138327930866480207815084989'),
  iv: Buffer.from('3134003223491201')
};

function aesEncrypt(text, key, iv) {
  const cipher = crypto.createCipheriv('aes-256-cbc', key, iv);
  let encrypted = cipher.update(text, 'utf8', 'base64');
  encrypted += cipher.final('base64');
  return encrypted;
}

function aesDecrypt(encrypted, key, iv) {
  try {
    const decipher = crypto.createDecipheriv('aes-256-cbc', key, iv);
    let decrypted = decipher.update(encrypted, 'base64', 'utf8');
    decrypted += decipher.final('utf8');
    return decrypted;
  } catch (e) {
    return '';
  }
}

async function extractGogoSources(embedUrl) {
  const sources = [];

  try {
    const parsed = new URL(embedUrl);
    const embedId = parsed.searchParams.get('id') || '';
    if (!embedId) return sources;

    const embedHost = parsed.hostname;
    const embedOrigin = parsed.protocol + '//' + embedHost;

    // Fetch the embed page to get the crypto token
    const embedResp = await fetch(embedUrl, {
      headers: { 'Referer': BASE_URL + '/' }
    });

    if (embedResp.statusCode !== 200) return sources;

    const embedHtml = embedResp.body;

    // Extract the encrypted token from the embed page
    const tokenMatch = /data-value=[""']([^""']+)[""']/i.exec(embedHtml);
    const scriptDataMatch = /class=[""']crypto-js[""'][^>]*data-value=[""']([^""']+)[""']/i.exec(embedHtml)
      || tokenMatch;

    if (!scriptDataMatch) {
      // Fallback: try to find direct m3u8 links in the embed page
      const m3u8Re = /https?:\/\/[^\s""'<>]+\.m3u8[^\s""'<>]*/gi;
      const m3u8Matches = embedHtml.match(m3u8Re);
      if (m3u8Matches) {
        for (const m3u of m3u8Matches) {
          sources.push({ url: m3u, quality: 'auto', isM3U8: true });
        }
      }
      return sources;
    }

    const encryptedToken = scriptDataMatch[1];

    // Decrypt the token
    const decryptedToken = aesDecrypt(encryptedToken, GOGOCDN_KEYS.key, GOGOCDN_KEYS.iv);

    // Build the AJAX request
    const encryptedId = aesEncrypt(embedId, GOGOCDN_KEYS.key, GOGOCDN_KEYS.iv);
    const ajaxParams = 'id=' + encodeURIComponent(encryptedId) + '&alias=' + embedId + '&' + decryptedToken;
    const ajaxUrl = embedOrigin + '/encrypt-ajax.php?' + ajaxParams;

    const ajaxResp = await fetch(ajaxUrl, {
      headers: {
        'X-Requested-With': 'XMLHttpRequest',
        'Referer': embedUrl,
        'Accept': 'application/json'
      }
    });

    if (ajaxResp.statusCode === 200) {
      try {
        const ajaxJson = JSON.parse(ajaxResp.body);
        const decryptedData = aesDecrypt(ajaxJson.data, GOGOCDN_KEYS.secondKey, GOGOCDN_KEYS.iv);

        if (decryptedData) {
          const sourceData = JSON.parse(decryptedData);

          // Extract from 'source' array (usually HLS)
          if (sourceData.source && Array.isArray(sourceData.source)) {
            for (const s of sourceData.source) {
              sources.push({
                url: s.file || s.url || '',
                quality: s.label || 'auto',
                isM3U8: (s.file || s.url || '').includes('.m3u8') || s.type === 'hls'
              });
            }
          }

          // Extract from 'source_bk' array (backup sources)
          if (sourceData.source_bk && Array.isArray(sourceData.source_bk)) {
            for (const s of sourceData.source_bk) {
              sources.push({
                url: s.file || s.url || '',
                quality: (s.label || 'backup') + ' (backup)',
                isM3U8: (s.file || s.url || '').includes('.m3u8') || s.type === 'hls'
              });
            }
          }
        }
      } catch (e) {
        console.error('Failed to parse AJAX response:', e.message);
      }
    }
  } catch (err) {
    console.error('GogoSource extraction error:', err.message);
  }

  return sources;
}

async function handleWatch(episodeId, res) {
  try {
    // Fetch the episode page to find the embed iframe
    const episodeUrl = BASE_URL + '/' + episodeId;
    const resp = await fetch(episodeUrl);

    if (resp.statusCode !== 200) {
      return jsonResponse(res, 503, { error: 'upstream unavailable' });
    }

    const html = resp.body;

    // Find the iframe embed URL (usually in div.play-video or similar)
    const iframeMatch = /<iframe[^>]*src=[""']([^""']*)[""']/i.exec(html);

    if (!iframeMatch) {
      return jsonResponse(res, 404, { error: 'no video source found for this episode' });
    }

    let embedUrl = iframeMatch[1].trim();
    if (embedUrl.startsWith('//')) embedUrl = 'https:' + embedUrl;
    if (!embedUrl.startsWith('http')) embedUrl = 'https://' + embedUrl;

    // Extract sources from the GogoCDN embed player
    const sources = await extractGogoSources(embedUrl);

    if (sources.length === 0) {
      // Last resort: try to find m3u8 links from the original page
      const m3u8Re = /https?:\/\/[^\s""'<>]+\.m3u8[^\s""'<>]*/gi;
      const directMatches = html.match(m3u8Re);
      if (directMatches) {
        for (const m3u of directMatches) {
          sources.push({ url: m3u, quality: 'auto', isM3U8: true });
        }
      }
    }

    jsonResponse(res, 200, {
      episodeId: episodeId,
      embedUrl: embedUrl,
      sources: sources
    });
  } catch (err) {
    console.error('Watch error:', err.message);
    jsonResponse(res, 503, { error: 'upstream unavailable', details: err.message });
  }
}

// ─── Router ──────────────────────────────────────────────────
const server = http.createServer(async (req, res) => {
  // Handle CORS preflight
  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type'
    });
    return res.end();
  }

  const parsed = url.parse(req.url, true);
  const pathname = decodeURIComponent(parsed.pathname || '/');

  // Route matching
  try {
    // Health check: GET /
    if (pathname === '/') {
      return jsonResponse(res, 200, {
        status: 'ok',
        message: 'UniversalMediaOS Scraper Active',
        endpoints: [
          '/anime/gogoanime/:query',
          '/anime/gogoanime/watch/:episodeId',
          '/anime/gogoanime/info/:animeId'
        ]
      });
    }

    // Search: GET /anime/gogoanime/:query
    const searchMatch = /^\/anime\/gogoanime\/([^\/]+)$/.exec(pathname);
    if (searchMatch && !pathname.includes('/watch/') && !pathname.includes('/info/')) {
      const query = decodeURIComponent(searchMatch[1]);
      return await handleSearch(query, res);
    }

    // Watch: GET /anime/gogoanime/watch/:episodeId
    const watchMatch = /^\/anime\/gogoanime\/watch\/(.+)$/.exec(pathname);
    if (watchMatch) {
      const episodeId = decodeURIComponent(watchMatch[1]);
      return await handleWatch(episodeId, res);
    }

    // Info: GET /anime/gogoanime/info/:animeId
    const infoMatch = /^\/anime\/gogoanime\/info\/(.+)$/.exec(pathname);
    if (infoMatch) {
      const animeId = decodeURIComponent(infoMatch[1]);
      return await handleInfo(animeId, res);
    }

    // 404 for unknown routes
    jsonResponse(res, 404, { error: 'not found', path: pathname });
  } catch (err) {
    console.error('Unhandled error:', err.message);
    jsonResponse(res, 500, { error: 'internal server error', details: err.message });
  }
});

server.listen(PORT, () => {
  console.log('[UniversalMediaOS Scraper] Real GogoAnime scraper running on http://localhost:' + PORT);
  console.log('[UniversalMediaOS Scraper] Endpoints:');
  console.log('  GET /                                     - Health check');
  console.log('  GET /anime/gogoanime/:query                - Search anime');
  console.log('  GET /anime/gogoanime/info/:animeId         - Get episode list');
  console.log('  GET /anime/gogoanime/watch/:episodeId      - Get streaming sources');
});
";
        }
    }
}
