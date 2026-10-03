#!/usr/bin/env python3
import base64
import html as html_lib
import json
import math
import os
import re
import shutil
import socket
import sys
import tempfile
import time
import unicodedata
import xml.etree.ElementTree as ET
from collections import deque
from concurrent.futures import ThreadPoolExecutor, as_completed
from urllib.parse import parse_qsl, quote_plus, urlencode, urljoin, urlparse

import curl_cffi.requests as cffi_req
from bs4 import BeautifulSoup
from DrissionPage import ChromiumOptions, ChromiumPage
from scraper import (build_replay_headers, collect_subtitle_tracks, is_safe_public_url,
                     subtitle_track, validate_hls_result, validate_media_payload)


USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
)
SEED_SITES = [
    "https://vidsrc.to",
    "https://vidsrc.me",
    "https://vidsrc.cc",
    "https://vidsrc.net",
    "https://vidsrc.xyz",
    "https://vidsrc.io",
    "https://embed.smashystream.com",
    "https://multiembed.mov",
    "https://autoembed.co",
    "https://2embed.cc",
]
VIDSRC_MIRRORS = [url for url in SEED_SITES if "vidsrc" in url]
MEDIA_EXTENSION_RE = re.compile(
    r"\.(?:m3u8|mpd|mp4|mkv|webm)(?:$|[?#])", re.I)
ABSOLUTE_MEDIA_RE = re.compile(
    r'https?://[^\s"\'<>\\]+\.(?:m3u8|mpd|mp4|mkv|webm)(?:[^\s"\'<>\\]*)?',
    re.I,
)
QUOTED_URL_RE = re.compile(
    r"(?:src|file|url|source|stream|hls|dash)\s*[:=]\s*[\"']([^\"']+)[\"']",
    re.I,
)
IFRAME_RE = re.compile(
    r'<(?:iframe|embed)\b[^>]*(?:src|data-src)=["\']([^"\']+)["\']',
    re.I,
)
ANCHOR_RE = re.compile(
    r'<a\b[^>]*href=["\']([^"\']+)["\'][^>]*>(.*?)</a>',
    re.I | re.S,
)
MEDIA_LISTEN_TARGETS = [
    "m3u8", "mpd", "mp4", "mkv", "webm", "mpegurl", "dash+xml",
    "playlist", "manifest",
]
REPLAY_HEADER_BLOCKLIST = {
    "host", "connection", "content-length", "transfer-encoding", "accept-encoding",
    "range", "te", "trailer", "upgrade",
}

# Observe the provider's decoded response before its player consumes it. Keep
# the original promise/value/error unchanged and never issue another request.
PLAYER_ITEM_CAPTURE = r"""
(() => {
    if (Object.prototype.hasOwnProperty.call(window, '__umosAvItems')) return;
    const items = [];
    Object.defineProperty(window, '__umosAvItems', {value: items});
    let decoder;
    Object.defineProperty(window, 'vsFetchJSON', {
        configurable: true,
        get() { return decoder; },
        set(fn) {
            if (typeof fn !== 'function') { decoder = fn; return; }
            decoder = function(...args) {
                const output = Reflect.apply(fn, this, args);
                Promise.resolve(output).then(value => {
                    try {
                        const d = value && value.data;
                        if (!d || typeof args[0] !== 'string' || args[0].length > 8192 ||
                            !d.title || !d.imdb_id || !d.file_name || !Array.isArray(d.stream_urls) ||
                            !d.stream_urls.length || d.stream_urls.length > 24 ||
                            ((value.status_code ?? value.status) != null && String(value.status_code ?? value.status) !== '200')) return;
                        const data = {};
                        for (const key of ['title', 'imdb_id', 'file_name', 'season', 'episode', 'stream_urls'])
                            if (d[key] != null) data[key] = d[key];
                        const item = JSON.parse(JSON.stringify({apiUrl: args[0], data,
                            default_subs: Array.isArray(value.default_subs) ? value.default_subs.slice(0,12) : []}));
                        if (new TextEncoder().encode(JSON.stringify(item)).length > 256*1024) return;
                        const previous = items.findIndex(saved => saved.apiUrl === item.apiUrl);
                        if (previous >= 0) items.splice(previous,1);
                        items.push(item);
                        if (items.length > 4) items.shift();
                    } catch (_) {}
                }, () => {});
                return output;
            };
        }
    });
})();
"""

PLAYER_ITEM_SCAN = r"""
return (() => {
    const jw = window.__JW;
    let activeCaption = null;
    try {
        if (jw && jw.SUB && jw.SUB.activeKey !== 'off' && jw.state.currentCues && jw.state.currentCues.length) {
            let key = 'va_subtitle_' + jw.CONFIG.mediaId;
            if (jw.CONFIG.mediaType === 'tv') key += '_S' + String(jw.state.season).padStart(2,'0') + 'E' + String(jw.state.episode).padStart(2,'0');
            const p = JSON.parse(localStorage.getItem(key) || 'null');
            if (p && p.vttUrl) {
                activeCaption = {url: p.vttUrl, label: p.label, language: p.lang, default: true};
                if (jw.state.currentCues.length <= 20000) {
                    const cues = jw.state.currentCues.map(c => ({start: c.start, end: c.end, text: c.text}));
                    if (JSON.stringify(cues).length <= 2*1024*1024) activeCaption.cues = cues;
                }
            }
        }
    } catch (_) {}
    let apiUrl = '';
    if (jw && jw.CONFIG && typeof window.vsFetchJSON === 'function') {
        if (jw.CONFIG.streamBase && jw.state.season != null && jw.state.episode != null)
            apiUrl = jw.CONFIG.streamBase + '&season=' + encodeURIComponent(jw.state.season) + '&episode=' + encodeURIComponent(jw.state.episode) + '&stream_urls';
        else apiUrl = jw.CONFIG.api || '';
    }
    return {apiUrl, activeCaption, items: window.__umosAvItems || [],
        streams: jw && jw.state ? jw.state.allStreams : [],
        captionFile: jw && jw.SUB ? jw.SUB.fileName : ''};
})();
"""
SEARCH_PATHS = (
    "/search?keyword={query}",
    "/search?q={query}",
    "/search?query={query}",
    "/search.html?keyword={query}",
    "/catalog?q={query}",
)


def attempt(function, default=None):
    try:
        return function()
    except Exception:
        return default


def normalize_text(value):
    value = unicodedata.normalize("NFKC", html_lib.unescape(str(value or ""))).casefold()
    return " ".join(re.findall(r"[^\W_]+", value, re.UNICODE))


def title_tokens(value):
    ignored = {"the", "and", "of", "a", "an", "movie", "tv", "television", "show"}
    tokens = [token for token in normalize_text(value).split() if token not in ignored]
    return tokens or normalize_text(value).split()


def host_label(url):
    host = (urlparse(url).hostname or "Provider").removeprefix("www.")
    return host.split(".")[0].replace("-", " ").title()


def normalize_url(value, base_url=""):
    value = html_lib.unescape(str(value or "").strip())
    value = value.replace("\\/", "/").replace("\\u0026", "&")
    if value.startswith("//"):
        return "https:" + value
    if value.startswith(("http://", "https://")):
        return value
    return urljoin(base_url, value) if base_url and value and not value.startswith(("blob:", "data:")) else ""


def is_media_url(url, content_type=""):
    lowered_type = str(content_type or "").lower()
    return bool(MEDIA_EXTENSION_RE.search(str(url or ""))) or any(marker in lowered_type for marker in (
        "mpegurl", "dash+xml", "video/mp4", "video/webm", "matroska",
    ))


def sanitize_headers(headers):
    values = {}
    for key, value in dict(headers or {}).items():
        name = str(key).strip()
        if not name or name.startswith(":") or name.lower() in REPLAY_HEADER_BLOCKLIST:
            continue
        values[name] = str(value).strip()
    return values


def stream_result(url, referer="", user_agent=USER_AGENT, cookie="", headers=None, requires_webview=False):
    return {
        "url": str(url),
        "user_agent": str(user_agent or USER_AGENT),
        "cookie": str(cookie or ""),
        "referer": str(referer or ""),
        "headers": sanitize_headers(headers),
        "requires_webview": bool(requires_webview),
    }


def extract_media_urls(text, base_url=""):
    if not text:
        return []
    normalized = html_lib.unescape(str(text)).replace("\\/", "/").replace("\\u0026", "&")
    urls = []

    def add(value):
        url = normalize_url(value, base_url)
        if url and is_media_url(url) and url not in urls:
            urls.append(url)

    for match in ABSOLUTE_MEDIA_RE.finditer(normalized):
        add(match.group(0))
    for match in QUOTED_URL_RE.finditer(normalized):
        add(match.group(1))
    for token in re.findall(r"[A-Za-z0-9+/]{48,}={0,2}", normalized)[:80]:
        decoded = attempt(lambda token=token: base64.b64decode(token + "===").decode("utf-8", "ignore"), "")
        for match in ABSOLUTE_MEDIA_RE.finditer(decoded):
            add(match.group(0))
    return urls


def extract_child_pages(text, base_url=""):
    pages = []
    for match in IFRAME_RE.finditer(text or ""):
        url = normalize_url(match.group(1), base_url)
        if is_player_page(url) and not is_media_url(url) and url not in pages:
            pages.append(url)
    for match in ANCHOR_RE.finditer(text or ""):
        href = normalize_url(match.group(1), base_url)
        marker = normalize_text(re.sub(r"<[^>]+>", " ", match.group(2)))
        if is_player_page(href) and re.search(r"embed|player|watch|play|video|stream|episode", f"{urlparse(href).path} {marker}", re.I):
            if not is_media_url(href) and href not in pages:
                pages.append(href)
    return pages


def is_player_page(url):
    if not is_safe_public_url(url):
        return False
    path = urlparse(url).path.lower()
    return not re.search(r"\.(?:js|css|png|gif|jpg|svg)(?:/|$)", path) and bool(
        re.search(r"(?:^|/)(?:embed|player|watch|play|video|stream|episode)(?:[./_-]|$)", path))


def embed_unit(url):
    path = urlparse(url).path.rstrip('/')
    match = re.search(r"/embed/(?:player/)?(movie|tv)/([A-Za-z0-9_-]+?)(?:/(\d+)[/-](\d+))?$", path, re.I)
    if match:
        return match.groups()
    match = re.search(r"/tv/(?:imdb|tmdb)/([A-Za-z0-9_]+)-(\d+)-(\d+)$", path, re.I)
    if match:
        return ('tv', *match.groups())
    match = re.search(r"/(?:movie/(?:imdb|tmdb)|embed)/((?:tt)?\d+)$", path, re.I)
    if match:
        return ('movie', match.group(1), None, None)
    pairs = dict(parse_qsl(urlparse(url).query))
    if pairs.get('video_id'):
        season, episode = pairs.get('s'), pairs.get('e')
        return ('tv' if season and episode else 'movie', pairs['video_id'], season, episode)
    return None


def compatible_player_page(url, requested):
    if not is_safe_public_url(url):
        return False
    wanted, actual = embed_unit(requested), embed_unit(url)
    if not wanted or not actual:
        return True
    if wanted[0].lower() != actual[0].lower():
        return False
    if wanted[1].lower().startswith('tt') and actual[1].lower().startswith('tt') and wanted[1].lower() != actual[1].lower():
        return False
    if wanted[2] and actual[2]:
        if not all(value and value.isdigit() for value in (*wanted[2:], *actual[2:])):
            return False
        if tuple(map(int, wanted[2:])) != tuple(map(int, actual[2:])):
            return False
    return True


DASH_NS = '{urn:mpeg:dash:schema:mpd:2011}'


def dash_fetch_bytes(url, headers, root_url, limit, deadline, sample=False):
    """Bound every body/redirect and replay credentials only to their captured origin."""
    def origin(value):
        parsed = urlparse(value)
        return parsed.scheme.lower(), parsed.hostname, parsed.port or (443 if parsed.scheme == 'https' else 80)
    captured_origin = origin(root_url)
    for _ in range(6):
        if time.monotonic() >= deadline or not is_safe_public_url(url):
            raise ValueError('dash_unsafe_or_expired')
        replay = dict(headers) if origin(url) == captured_origin else {
            key: value for key, value in headers.items() if key.lower() in ('user-agent', 'accept', 'accept-encoding')}
        if sample:
            replay['Range'] = f'bytes=0-{limit-1}'
        response = None
        try:
            response = cffi_req.get(url, impersonate='chrome120', headers=replay, stream=True,
                allow_redirects=False, timeout=min(4, max(.1, deadline-time.monotonic())))
            if response.status_code in (301, 302, 303, 307, 308):
                location = response.headers.get('location', '')
                if not location:
                    raise ValueError('dash_redirect_missing')
                url = urljoin(url, location)
                continue
            if response.status_code not in (200, 206) or not is_safe_public_url(getattr(response, 'url', url)):
                raise ValueError('dash_resource_unavailable')
            if response.status_code == 206 and not re.match(r'bytes 0-\d+/\d+', response.headers.get('content-range', '')):
                raise ValueError('dash_wrong_sample_range')
            data = bytearray()
            for chunk in response.iter_content(chunk_size=16384):
                if time.monotonic() >= deadline:
                    raise ValueError('dash_body_deadline')
                if not chunk:
                    continue
                if sample:
                    data.extend(chunk[:limit-len(data)])
                    if len(data) >= limit:
                        break
                else:
                    if len(data) + len(chunk) > limit:
                        raise ValueError('dash_manifest_limit')
                    data.extend(chunk)
            expected = response.headers.get('content-length', '')
            if expected.isdigit() and len(data) < min(limit, int(expected)):
                raise ValueError('dash_truncated_body')
            return bytes(data), getattr(response, 'url', url)
        finally:
            if response is not None:
                response.close()
    raise ValueError('dash_redirect_limit')


def dash_duration(value, fallback=None):
    if value is None:
        return fallback
    match = re.fullmatch(r'P(?:(\d+)D)?T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?', value)
    if not match or not any(match.groups()):
        raise ValueError('dash_duration')
    days, hours, minutes, seconds = (float(part or 0) for part in match.groups())
    return days*86400 + hours*3600 + minutes*60 + seconds


def dash_integer(attributes, name, fallback=0):
    value = attributes.get(name)
    if value is None:
        return fallback
    if not re.fullmatch(r'-?\d{1,16}', value) or abs(int(value)) > 9_000_000_000_000_000:
        raise ValueError('dash_number')
    return int(value)


def dash_schedule(template, attributes, duration):
    scale = dash_integer(attributes, 'timescale', 1)
    end = duration*scale
    if scale <= 0 or not math.isfinite(end) or end > 9e15 or dash_integer(attributes, 'presentationTimeOffset') != 0:
        raise ValueError('dash_timescale_or_offset')
    timeline = next((node.find(DASH_NS+'SegmentTimeline') for node in reversed(template)
                     if node.find(DASH_NS+'SegmentTimeline') is not None), None)
    segments, cursor = [], 0
    entries = list(timeline) if timeline is not None else []
    if timeline is None:
        length = dash_integer(attributes, 'duration')
        if length <= 0 or math.ceil(end/length) > 20000:
            raise ValueError('dash_schedule_limit')
        while cursor < end:
            segments.append((cursor, length))
            cursor += length
    else:
        if not 0 < len(entries) <= 20000 or any(node.tag != DASH_NS+'S' for node in entries):
            raise ValueError('dash_timeline')
        for index, entry in enumerate(entries):
            start, length, repeat = (dash_integer(entry.attrib, 't', cursor),
                                     dash_integer(entry.attrib, 'd'), dash_integer(entry.attrib, 'r'))
            if start != cursor or length <= 0 or repeat < -1:
                raise ValueError('dash_gap_or_overlap')
            if repeat == -1:
                stop = dash_integer(entries[index+1].attrib, 't', -1) if index+1 < len(entries) else end
                repeat = math.ceil((stop-start)/length)-1
            if repeat < 0 or repeat >= 20000-len(segments):
                raise ValueError('dash_repeats')
            for _ in range(repeat+1):
                segments.append((cursor, length))
                cursor += length
        if cursor < end-scale*.1 or not segments or segments[-1][0] >= end:
            raise ValueError('dash_incomplete_timeline')
    return segments


def dash_expand(value, representation, number, timestamp):
    value = value.replace('$$', '\x01')
    def expand(match):
        name, width = match.groups()
        item = {'RepresentationID': representation.get('id'), 'Bandwidth': representation.get('bandwidth', '0'),
                'Number': str(number), 'Time': str(timestamp)}[name]
        if item is None:
            raise ValueError('dash_representation_id')
        return item.zfill(int(width)) if width and name != 'RepresentationID' else item
    value = re.sub(r'\$(RepresentationID|Bandwidth|Number|Time)(?:%0([1-9])d)?\$', expand, value)
    if '$' in value:
        raise ValueError('dash_unsupported_template')
    return value.replace('\x01', '$')


def dash_mp4_prefix(data):
    return len(data) >= 8 and int.from_bytes(data[:4], 'big') >= 8 and data[4:8] in (b'ftyp', b'styp', b'moof', b'moov')


def validate_dash_result(url, headers, result, deadline):
    """Qualify finite MP4 templates using init AND media bytes; XML size is not proof."""
    deadline = min(deadline, time.monotonic()+12)
    try:
        data, effective = dash_fetch_bytes(url, headers, url, 2*1024*1024, deadline)
        if b'\x00' in data or b'<!DOCTYPE' in data.upper() or b'<!ENTITY' in data.upper():
            return False
        root = ET.fromstring(data)
        if root.tag != DASH_NS+'MPD' or root.get('type', 'static') != 'static':
            return False
        if any(node.tag.rsplit('}', 1)[-1] in ('ContentProtection', 'SegmentBase', 'SegmentList') or
               any(key.startswith('{http://www.w3.org/1999/xlink}') for key in node.attrib) for node in root.iter()):
            return False
        periods = root.findall(DASH_NS+'Period')
        if len(periods) != 1 or dash_duration(periods[0].get('start'), 0) != 0:
            return False
        period = periods[0]
        duration = dash_duration(period.get('duration'), dash_duration(root.get('mediaPresentationDuration')))
        declared = dash_duration(root.get('mediaPresentationDuration'), duration)
        if duration is None or not math.isfinite(duration) or duration <= 0 or abs(duration-declared) > max(1, duration*.001):
            return False
        selected = []
        for group in period.findall(DASH_NS+'AdaptationSet'):
            representations = group.findall(DASH_NS+'Representation')
            if not 0 < len(representations) <= 128:
                return False
            kind = group.get('contentType') or (group.get('mimeType') or representations[0].get('mimeType', '')).split('/')[0]
            if kind not in ('video', 'audio') or (kind == 'video' and any(track[2] == 'video' for track in selected)):
                return False
            languages = {}
            for representation in representations:
                language = representation.get('lang', group.get('lang', ''))
                if language not in languages or dash_integer(representation.attrib, 'bandwidth') > dash_integer(languages[language].attrib, 'bandwidth'):
                    languages[language] = representation
            for representation in languages.values():
                if representation.get('mimeType', group.get('mimeType')) != kind+'/mp4':
                    return False
                selected.append((group, representation, kind))
        if not 1 < len(selected) <= 16 or sum(kind == 'video' for _, _, kind in selected) != 1 or not any(kind == 'audio' for _, _, kind in selected):
            return False
        samples, count = [], 0
        for group, representation, _ in selected:
            ancestry = (root, period, group, representation)
            base = effective
            templates = []
            attributes = {}
            for ancestor in ancestry:
                bases = ancestor.findall(DASH_NS+'BaseURL')
                if len(bases) > 1:
                    return False
                if bases:
                    base = urljoin(base, (bases[0].text or '').strip())
                templates.extend(ancestor.findall(DASH_NS+'SegmentTemplate'))
            for template in templates:
                attributes.update(template.attrib)
            if not templates or not attributes.get('initialization') or not attributes.get('media'):
                return False
            schedule = dash_schedule(templates, attributes, duration)
            count += len(schedule)+1
            if count > 20000:
                return False
            number = dash_integer(attributes, 'startNumber', 1)
            init = urljoin(base, dash_expand(attributes['initialization'], representation.attrib, 0, 0))
            media = [urljoin(base, dash_expand(attributes['media'], representation.attrib, number+i, start))
                     for i, (start, _) in enumerate(schedule)]
            if not is_safe_public_url(init) or any(not is_safe_public_url(item) for item in media):
                return False
            samples.extend((init, media[0]))
        for sample in dict.fromkeys(samples):
            data, _ = dash_fetch_bytes(sample, headers, url, 65536, deadline, sample=True)
            if not dash_mp4_prefix(data):
                return False
        result['content_type'] = 'application/dash+xml'
        # MPD language labels do not independently establish spoken language.
        result['audio_languages'] = []
        return True
    except Exception:
        return False


def validated_stream(result, deadline):
    if not result or result.get('requires_webview') or time.monotonic() >= deadline:
        return None
    url = result.get('url', '')
    headers = build_replay_headers(result)
    if not is_safe_public_url(url):
        return None
    valid = validate_dash_result(url, headers, result, deadline) \
        if '.mpd' in urlparse(url).path.lower() or 'dash+xml' in result.get('content_type', '').lower() \
        else validate_hls_result(url, headers, result, inspect_alternate_audio=False, deadline=deadline) \
        if '.m3u8' in urlparse(url).path.lower() or 'mpegurl' in result.get('content_type', '').lower() \
        else validate_media_payload(url, headers, result, deadline=deadline)
    if valid:
        result['media_validated'] = True
        return result
    print('[Audiovisual] Media candidate failed byte validation; continuing native extraction.', file=sys.stderr, flush=True)
    return None


def stream_belongs_to_item(url, data):
    actual = urlparse(url)
    for raw in data.get('stream_urls', []) if isinstance(data.get('stream_urls'), list) else []:
        declared = urlparse(str(raw))
        if (actual.scheme, actual.netloc, actual.path) != (declared.scheme, declared.netloc, declared.path):
            continue
        expected = parse_qsl(declared.query, keep_blank_values=True)
        observed = parse_qsl(actual.query, keep_blank_values=True)
        # This player appends only a per-host 'token' to its returned master.
        # All content-bearing query fields and the signed path must still agree.
        if not any(key == 'token' for key, _ in expected):
            observed = [(key, value) for key, value in observed if key != 'token']
        else:
            wildcard = any(key == 'token' and value == '__TOKEN__' for key, value in expected)
            if wildcard:
                expected = [(key, value) for key, value in expected if key != 'token']
                observed = [(key, value) for key, value in observed if key != 'token']
        if sorted(observed) == sorted(expected):
            return True
    return False


def provider_item_evidence(data):
    if not isinstance(data, dict):
        return None
    imdb = str(data.get('imdb_id') or '').strip().lower()
    title = str(data.get('title') or '').strip()
    filename = str(data.get('file_name') or '').replace('\\', '/').rsplit('/', 1)[-1]
    if not re.fullmatch(r'tt\d+', imdb) or not title or not filename:
        return None
    title_year = re.fullmatch(r'(.+?)\s+[([]?((?:18|19|20)\d{2})[)\]]?', title)
    year = int(title_year.group(2)) if title_year else None
    name = title_year.group(1).strip() if title_year else title
    numbered = re.search(r'(?i)(?<![A-Z0-9])S(\d{1,2})[ ._-]*E(\d{1,3})(?![A-Z0-9])', filename)
    season, episode = data.get('season'), data.get('episode')
    if season is not None or episode is not None or numbered:
        if season is not None and episode is not None:
            if not str(season).isdigit() or not str(episode).isdigit():
                raise ValueError('invalid_provider_episode')
            unit = (int(season), int(episode))
            if numbered and tuple(map(int, numbered.groups())) != unit:
                raise ValueError('provider_filename_episode_conflict')
        elif numbered:
            unit = tuple(map(int, numbered.groups()))
        else:
            return None
        kind, form = 'Television', 'Series'
        evidence_unit = {'SeasonNumber': unit[0], 'EpisodeNumber': unit[1]}
    else:
        # A movie release needs its independently returned title/year and file,
        # not merely a /movie request route or an echoed CONFIG.mediaType.
        normalized_file, normalized_name = normalize_text(filename), normalize_text(name)
        release = normalized_file[len(normalized_name):] if normalized_file.startswith(normalized_name + ' ') else normalized_file
        file_year = re.search(r'\b((?:18|19|20)\d{2})\b', release)
        if not year or not file_year or not re.search(r'(?i)\.(mp4|mkv|webm|avi|mov)$', filename):
            return None
        if int(file_year.group(1)) != year:
            raise ValueError('provider_filename_year_conflict')
        kind, form, evidence_unit = 'Movie', 'Feature', {}
    return {'Origin': 'ProviderItem', 'Identity': {
        'Kind': kind, 'ContentForm': form, 'Title': name, 'Year': year, 'ImdbId': imdb},
        'Unit': evidence_unit}


def loaded_caption_vtt(cues):
    """Reuse bounded, timed cues already loaded for the independently bound item."""
    if not isinstance(cues, list) or not 0 < len(cues) <= 20000:
        return ''
    blocks = []
    size = 8
    for index, cue in enumerate(cues):
        if not isinstance(cue, dict):
            return ''
        start, end, text = cue.get('start'), cue.get('end'), cue.get('text')
        if type(start) not in (int, float) or type(end) not in (int, float) or \
                not math.isfinite(start) or not math.isfinite(end) or not 0 <= start < end or \
                not isinstance(text, str) or not text.strip() or '\0' in text:
            return ''
        try:
            start_ms, end_ms = round(start*1000), round(end*1000)
        except (OverflowError, ValueError):
            return ''
        if start_ms >= end_ms:
            return ''
        def timestamp(millis):
            hours, remainder = divmod(millis, 3600000)
            minutes, remainder = divmod(remainder, 60000)
            seconds, millis = divmod(remainder, 1000)
            return f'{hours:02}:{minutes:02}:{seconds:02}.{millis:03}'
        body = html_lib.escape(re.sub(r'\n\s*\n', '\n', text.replace('\r', '')).strip(), quote=False)
        block = f'{index+1}\n{timestamp(start_ms)} --> {timestamp(end_ms)}\n{body}\n\n'
        size += len(block.encode('utf-8'))
        if size > 2*1024*1024:
            return ''
        blocks.append(block)
    return 'WEBVTT\n\n'+''.join(blocks)


def caption_has_cues(track, deadline):
    if time.monotonic() >= deadline:
        print('[Audiovisual] Caption validation skipped: caption deadline exhausted.', file=sys.stderr, flush=True)
        return False
    response = None
    try:
        response = cffi_req.get(track['url'], impersonate='chrome120',
            headers=build_replay_headers(track), stream=True, allow_redirects=False,
            timeout=min(2, max(.1, deadline-time.monotonic())))
        if response.status_code >= 300:
            print(f'[Audiovisual] Caption validation failed: HTTP {response.status_code}.', file=sys.stderr, flush=True)
            return False
        payload = b''
        for chunk in response.iter_content(chunk_size=4096):
            if time.monotonic() >= deadline:
                return False
            payload += chunk
            if len(payload) >= 32768:
                break
        text = payload.decode('utf-8-sig', errors='replace')
        if re.search(r'<(?:html|!doctype|script)\b', text, re.I):
            return False
        return bool(re.search(r'(?:\d{2}:)?\d{2}:\d{2}[.,]\d{3}\s+-->\s+(?:\d{2}:)?\d{2}:\d{2}[.,]\d{3}', text))
    except Exception:
        return False
    finally:
        if response is not None:
            attempt(response.close)


def enrich_native_result(result, document, requested, deadline):
    snapshot = attempt(lambda: document.run_js(PLAYER_ITEM_SCAN), {}) or {}
    if not isinstance(snapshot, dict):
        snapshot = {}
    item = None
    api_url = normalize_url(snapshot.get('apiUrl'), getattr(document, 'url', ''))
    if is_safe_public_url(api_url) and stream_belongs_to_item(result['url'], {'stream_urls': snapshot.get('streams')}) \
            and time.monotonic() < deadline:
        # Only an actually decoded response for this API, active release and
        # current media can supply cached evidence. CONFIG is request context.
        saved_items = snapshot.get('items')
        for saved in reversed(saved_items[-4:] if isinstance(saved_items, list) else []):
            if isinstance(saved, dict) and isinstance(saved.get('data'), dict) and \
                    normalize_url(saved.get('apiUrl'), getattr(document, 'url', '')) == api_url and \
                    saved['data'].get('file_name') == snapshot.get('captionFile') and snapshot.get('captionFile') and \
                    stream_belongs_to_item(result['url'], saved['data']):
                item = saved
                break
    if item is None and is_safe_public_url(api_url) and \
            stream_belongs_to_item(result['url'], {'stream_urls': snapshot.get('streams')}) and time.monotonic() < deadline:
        # Read the selected player's existing API through its own JSON decoder.
        # Never turn CONFIG's requested IDs into evidence. Bind the response's
        # file name to the active item and its master URL to the current player.
        seconds = min(2, max(.1, deadline-time.monotonic()))
        observed = attempt(lambda: document.run_js("""
            return Promise.race([window.vsFetchJSON(arguments[0]).catch(() => null),
                new Promise(resolve => setTimeout(() => resolve(null), arguments[1]))]);
            """, api_url, int(seconds*1000), timeout=seconds+.5))
        if isinstance(observed, dict) and isinstance(observed.get('data'), dict) and \
                observed['data'].get('file_name') == snapshot.get('captionFile') and snapshot.get('captionFile'):
            item = observed
    evidence = None
    if item:
        try:
            evidence = provider_item_evidence(item['data'])
        except ValueError:
            print('[Audiovisual] Provider item contradicts its release file; continuing extraction.', file=sys.stderr, flush=True)
            return None
        wanted = embed_unit(requested)
        if evidence and wanted:
            identity, unit = evidence['Identity'], evidence['Unit']
            if (identity['Kind'] == 'Movie') != (wanted[0].lower() == 'movie') or \
                    (wanted[1].lower().startswith('tt') and identity['ImdbId'] != wanted[1].lower()) or \
                    (wanted[2] and (unit.get('SeasonNumber'), unit.get('EpisodeNumber')) != tuple(map(int, wanted[2:]))):
                return None
    caption_deadline = min(deadline, time.monotonic()+3)
    # The stream URL can appear before this player's automatic caption request
    # finishes. Wait only for that active item's loaded cues, within the same
    # caption/operation budget, rather than treating the early empty list as final.
    if item and not item.get('default_subs') and not snapshot.get('activeCaption'):
        # This provider normally starts captions only after its browser video is
        # ready. Native media can work even while that browser's selected HLS
        # quality fails, so request its existing caption loader for this file.
        attempt(lambda: document.run_js("""
            if (window.JWSubs && typeof JWSubs.auto === 'function' && window.__JW &&
                __JW.SUB.fileName === arguments[0] && window.__umosAvCaptionFile !== arguments[0]) {
                window.__umosAvCaptionFile = arguments[0]; JWSubs.auto();
            }
            """, item['data']['file_name'], timeout=.5))
        while time.monotonic() < caption_deadline:
            refreshed = attempt(lambda: document.run_js(PLAYER_ITEM_SCAN, timeout=.5), {}) or {}
            if not isinstance(refreshed, dict) or refreshed.get('captionFile') != item['data'].get('file_name'):
                break
            snapshot = refreshed
            if snapshot.get('activeCaption'):
                break
            time.sleep(min(.15, max(0, caption_deadline-time.monotonic())))
    tracks = attempt(lambda: collect_subtitle_tracks(document, result.get('headers')), []) or []
    if item:
        for raw in item.get('default_subs', []):
            if isinstance(raw, dict):
                track = subtitle_track({**raw, 'language': raw.get('language') or raw.get('lang', '')},
                                       document, document.url)
                if track:
                    tracks.append(track)
        active = snapshot.get('activeCaption')
        if snapshot.get('captionFile') == item['data'].get('file_name') and isinstance(active, dict):
            track = subtitle_track(active, document, document.url)
            if track:
                content = loaded_caption_vtt(active.get('cues'))
                if content:
                    track['inline_vtt'] = content
                tracks.insert(0, track)
    unique = {track['url']: track for track in reversed(tracks)}
    checked = []
    for track in sorted(unique.values(), key=lambda track: (not track.get('default'),
            not (track.get('language', '').lower() in ('en', 'eng', 'ar', 'ara'))))[:12]:
        if track.get('inline_vtt') or caption_has_cues(track, caption_deadline):
            supplied_language = track.get('language', '').strip()
            track['language'] = {'english': 'en', 'arabic': 'ar'}.get(supplied_language.lower(), supplied_language)
            if re.search(r'(?i)\.(srt|vtt|ass)$', track.get('label', '')) and supplied_language:
                track['label'] = supplied_language
            checked.append(track)
    if checked:
        result['subtitles'] = checked
    elif item:
        print(f'[Audiovisual] No timed captions returned: player_ready={bool(snapshot.get("activeCaption"))}, '
              f'candidates={len(unique)}, remaining={max(0, caption_deadline-time.monotonic()):.3f}s.',
              file=sys.stderr, flush=True)
    if evidence:
        if result.get('audio_languages'):
            evidence['Audio'] = {'Origin': 'ObservedStream', 'Languages': result['audio_languages']}
        languages = list(dict.fromkeys(track['language'] for track in checked if track.get('language')))
        if languages:
            evidence['Subtitles'] = {'Origin': 'ProviderItem', 'Languages': languages}
        result['evidence'] = evidence
    return result


def split_catalog_id(query):
    match = re.fullmatch(r"\s*(tmdb|imdb)\s*:\s*([A-Za-z0-9_-]+)\s*", query or "", re.I)
    return (match.group(1).lower(), match.group(2)) if match else ("", "")


def build_embed_results(catalog, media_id, kind, season, episode, base_url):
    root = base_url.rstrip("/")
    kind = str(kind or "movie").strip().lower()
    season = str(season or "").strip()
    episode = str(episode or "").strip()
    episodic = kind in ("tv", "television", "cartoon") and bool(season and episode)
    if episodic:
        label = f"S{season}E{episode}"
        return [
            {"title": f"Vidsrc {label}", "provider": "Vidsrc", "url": f"{root}/embed/tv/{media_id}/{season}/{episode}"},
            {"title": f"2Embed {label}", "provider": "2Embed", "url": f"https://2embed.cc/embed/tv/{media_id}/{season}/{episode}"},
            {"title": f"MultiEmbed {label}", "provider": "MultiEmbed", "url": f"https://multiembed.mov/?video_id={media_id}&s={season}&e={episode}"},
            {"title": f"AutoEmbed {label}", "provider": "AutoEmbed", "url": f"https://autoembed.co/tv/{catalog}/{media_id}-{season}-{episode}"},
        ]
    if kind in ("tv", "television"):
        return []
    return [
        {"title": "Vidsrc Feature", "provider": "Vidsrc", "url": f"{root}/embed/movie/{media_id}"},
        {"title": "2Embed Feature", "provider": "2Embed", "url": f"https://2embed.cc/embed/{media_id}"},
        {"title": "MultiEmbed Feature", "provider": "MultiEmbed", "url": f"https://multiembed.mov/?video_id={media_id}"},
        {"title": "AutoEmbed Feature", "provider": "AutoEmbed", "url": f"https://autoembed.co/movie/{catalog}/{media_id}"},
    ]


def kind_terms(kind):
    kind = str(kind or "movie").lower()
    if kind == "cartoon":
        return ("cartoon", "animation", "animated")
    if kind in ("tv", "television"):
        return ("television", "series", "season", "episode")
    return ("movie", "film", "feature", "cinema")


def episode_variants(url, season, episode):
    season = str(season or "").strip()
    episode = str(episode or "").strip()
    if not season or not episode:
        return [url]
    parsed = urlparse(url)
    variants = []

    def add(value):
        if value and value not in variants:
            variants.append(value)

    path = parsed.path
    replacements = (
        (r"(?i)/season/\d+/episode/\d+", f"/season/{season}/episode/{episode}"),
        (r"(?i)/s\d+e\d+", f"/s{season}e{episode}"),
        (r"(?i)/\d+/\d+/?$", f"/{season}/{episode}"),
    )
    for pattern, replacement in replacements:
        if re.search(pattern, path):
            add(parsed._replace(path=re.sub(pattern, replacement, path)).geturl())

    pairs = parse_qsl(parsed.query, keep_blank_values=True)
    mapped = []
    found_season = found_episode = False
    for key, value in pairs:
        if key.lower() in ("s", "season"):
            value, found_season = season, True
        if key.lower() in ("e", "ep", "episode"):
            value, found_episode = episode, True
        mapped.append((key, value))
    if found_season or found_episode:
        if not found_season:
            mapped.append(("season", season))
        if not found_episode:
            mapped.append(("episode", episode))
        add(parsed._replace(query=urlencode(mapped)).geturl())
    neutral_pairs = [
        (key, value) for key, value in pairs
        if key.lower() not in ("s", "season", "e", "ep", "episode")
    ]
    add(parsed._replace(query=urlencode(neutral_pairs + [("season", season), ("episode", episode)])).geturl())
    base_path = re.sub(r"(?i)/(?:season/\d+/episode/\d+|s\d+e\d+|\d+/\d+)/?$", "", path).rstrip("/")
    add(parsed._replace(path=f"{base_path}/season/{season}/episode/{episode}", query=urlencode(neutral_pairs)).geturl())
    add(parsed._replace(path=f"{base_path}/s{season}e{episode}", query=urlencode(neutral_pairs)).geturl())
    return variants


def candidate_score(url, context, query, kind, year, season, episode):
    normalized_context = normalize_text(context)
    haystack = normalize_text(f"{url} {context}")
    tokens = title_tokens(query)
    matched = sum(token in haystack.split() for token in tokens)
    required = min(2, len(tokens))
    if matched < required:
        return 0
    score = matched * 10
    if normalize_text(query) in haystack:
        score += 20
    normalized_kind = str(kind).lower()
    query_token_set = set(title_tokens(query))
    context_tokens = set(normalized_context.split()) - query_token_set
    route_segments = {
        normalize_text(segment)
        for segment in urlparse(url).path.strip("/").split("/")
        if segment
    }
    cartoon_signal = (
        bool(route_segments & {"cartoon", "animation", "animated"})
        or any(signal in context_tokens for signal in kind_terms("cartoon"))
    )
    movie_signal = (
        bool(route_segments & {"movie", "movies", "film", "films", "feature"})
        or any(signal in context_tokens for signal in kind_terms("movie"))
    )
    television_signal = (
        bool(route_segments & {"tv", "show", "shows", "series", "season", "episode"})
        or any(signal in context_tokens for signal in kind_terms("television"))
    )
    if normalized_kind != "cartoon" and cartoon_signal:
        return 0
    if normalized_kind == "cartoon" and cartoon_signal:
        score += 8
    elif normalized_kind == "movie" and movie_signal and not television_signal:
        score += 8
    elif normalized_kind in ("tv", "television") and television_signal and not movie_signal:
        score += 8
    elif normalized_kind in ("movie", "cartoon", "tv", "television"):
        return 0
    title_numbers = set(re.findall(r"\b(?:18|19|20)\d{2}\b", normalize_text(query)))
    candidate_years = set(re.findall(r"\b(?:18|19|20)\d{2}\b", normalized_context)) - title_numbers
    if year and candidate_years and str(year) not in candidate_years:
        return 0
    if year and str(year) in candidate_years:
        score += 6
    if season and episode and re.search(
            rf"(?:s0*{re.escape(str(season))}e0*{re.escape(str(episode))}|season\s*0*{re.escape(str(season))}.*episode\s*0*{re.escape(str(episode))})",
            haystack, re.I):
        score += 12
    return score


def parse_search_links(page_html, base_url, query, kind, year, season, episode):
    candidates = []
    soup = BeautifulSoup(page_html or "", "lxml")
    for anchor in soup.select("a[href]"):
        href = normalize_url(anchor.get("href"), base_url)
        if not href or is_media_url(href):
            continue
        path = (urlparse(href).path or "").lower()
        if not re.search(r"/(?:watch|play|embed|movie|tv|show|series|episode|info)(?:/|$)", path):
            continue
        card = anchor
        for parent in anchor.parents:
            if getattr(parent, "name", "") in ("body", "html", "[document]"):
                break
            classes = " ".join(parent.get("class", [])) if hasattr(parent, "get") else ""
            if parent.name in ("article", "li") or re.search(r"card|result|item|poster|film|movie|show", classes, re.I):
                card = parent
                break
        context = card.get_text(" ", strip=True)
        score = candidate_score(href, context, query, kind, year, season, episode)
        if score <= 0:
            continue
        for index, variant in enumerate(episode_variants(href, season, episode)):
            candidates.append((score - index, variant))
    candidates.sort(key=lambda item: item[0], reverse=True)
    results = []
    for _, url in candidates:
        if url not in results:
            results.append(url)
    return results


def search_site(site_url, query, kind, year, season, episode, deadline):
    encoded_query = quote_plus(" ".join(part for part in (
        str(query).strip(), str(year or "").strip(), kind_terms(kind)[0]) if part))
    root = f"{urlparse(site_url).scheme}://{urlparse(site_url).netloc}".rstrip("/")
    for path in SEARCH_PATHS:
        if time.monotonic() >= deadline:
            break
        url = root + path.format(query=encoded_query)
        response = attempt(lambda url=url: cffi_req.get(
            url,
            impersonate="chrome124",
            headers={"User-Agent": USER_AGENT},
            timeout=min(2.5, max(.5, deadline - time.monotonic())),
        ))
        if not response or response.status_code >= 400:
            continue
        links = parse_search_links(response.text, str(response.url), query, kind, year, season, episode)
        if links:
            return links[:4]
    return []


def do_search(query, kind="movie", year=None, season=None, episode=None, base_scraper_url="https://vidsrc.to"):
    catalog, media_id = split_catalog_id(query)
    if catalog:
        return build_embed_results(catalog, media_id, kind, season, episode, base_scraper_url)

    sites = []
    for site in [base_scraper_url, *SEED_SITES]:
        root = site.rstrip("/")
        if root not in sites:
            sites.append(root)
    deadline = time.monotonic() + 14
    collected = []
    with ThreadPoolExecutor(max_workers=5) as executor:
        futures = {
            executor.submit(search_site, site, query, kind, year, season, episode, deadline): index
            for index, site in enumerate(sites[:10])
        }
        for future in as_completed(futures):
            links = attempt(future.result, [])
            if links:
                collected.append((futures[future], sites[futures[future]], links))

    results = []
    seen = set()
    for _, site, links in sorted(collected, key=lambda item: item[0]):
        for link in links:
            if link in seen:
                continue
            seen.add(link)
            results.append({"title": str(query), "provider": host_label(site), "url": link})
            if len(results) >= 24:
                return results
    return results


def response_cookie(response):
    jar = attempt(lambda: response.cookies.jar, [])
    return "; ".join(f"{cookie.name}={cookie.value}" for cookie in jar)


def static_page(url, timeout):
    response = cffi_req.get(
        url,
        impersonate="chrome124",
        headers={"User-Agent": USER_AGENT},
        timeout=timeout,
        allow_redirects=True,
    )
    final_url = str(response.url)
    content_type = response.headers.get("content-type", "")
    headers = getattr(getattr(response, "request", None), "headers", {})
    if is_media_url(final_url, content_type):
        return stream_result(final_url, url, USER_AGENT, response_cookie(response), headers), []
    media = extract_media_urls(response.text, final_url)
    if media:
        return stream_result(media[0], final_url, USER_AGENT, response_cookie(response), headers), []
    return None, extract_child_pages(response.text, final_url)


def static_waterfall(targets, deadline):
    queue = deque((target, 0) for target in targets)
    visited = set()
    browser_targets = list(targets)
    while queue and len(visited) < 6 and time.monotonic() < deadline:
        url, depth = queue.popleft()
        if url in visited:
            continue
        visited.add(url)
        if not compatible_player_page(url, targets[0]):
            continue
        if is_media_url(url):
            result = validated_stream(stream_result(url, url), deadline)
            if result:
                return result, browser_targets
            continue
        outcome = attempt(lambda url=url: static_page(
            url, min(4.0, max(.5, deadline - time.monotonic()))))
        if not outcome:
            continue
        result, children = outcome
        if result:
            result = validated_stream(result, deadline)
            if result:
                return result, browser_targets
        for child in reversed(children[:4]):
            if not compatible_player_page(child, targets[0]):
                continue
            if child not in browser_targets:
                browser_targets.append(child)
            if depth < 1:
                queue.appendleft((child, depth + 1))
    return None, browser_targets


def install_item_capture(page):
    # Cross-site players are separate renderer targets. A root-page init script
    # alone misses their initial API response; install before each target runs.
    driver = page.browser._driver
    targets = [{'type': 'page', 'exclude': False}, {'type': 'iframe', 'exclude': False}, {'exclude': True}]
    def attached(sessionId, targetInfo, **_):
        try:
            if targetInfo.get('type') in ('page', 'iframe'):
                driver.run('Page.enable', sessionId=sessionId, _timeout=1)
                driver.run('Page.addScriptToEvaluateOnNewDocument', source=PLAYER_ITEM_CAPTURE,
                           sessionId=sessionId, _timeout=1)
                driver.run('Target.setAutoAttach', autoAttach=True, waitForDebuggerOnStart=True,
                           flatten=True, filter=targets, sessionId=sessionId, _timeout=1)
        except Exception:
            print('[Audiovisual] Frame item observation unavailable; retaining metadata fallback.', file=sys.stderr, flush=True)
        finally:
            # Even if observation fails, never leave an owned player paused.
            attempt(lambda: driver.run('Runtime.runIfWaitingForDebugger', sessionId=sessionId, _timeout=1))
    driver.set_callback('Target.attachedToTarget', attached, immediate=True)
    outcome = driver.run('Target.setAutoAttach', autoAttach=True, waitForDebuggerOnStart=True,
                         flatten=True, filter=targets, _timeout=1)
    if outcome.get('error'):
        print('[Audiovisual] Original item observation unavailable; retaining metadata fallback.', file=sys.stderr, flush=True)


def launch_browser():
    data_root = os.environ.get("UNIVERSAL_MEDIA_OS_DATA_ROOT", "").strip()
    if data_root and not os.path.isabs(data_root):
        raise ValueError("UNIVERSAL_MEDIA_OS_DATA_ROOT must be an absolute path")
    profile_root = os.path.join(data_root, "Local", "UniversalMediaOS", "BrowserProfiles") if data_root else tempfile.gettempdir()
    os.makedirs(profile_root, exist_ok=True)
    profile = tempfile.mkdtemp(prefix="umos_av_", dir=profile_root)
    options = ChromiumOptions()
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        options.set_local_port(sock.getsockname()[1])
    options.new_env(True)
    options.set_user_data_path(profile)
    options.set_tmp_path(profile)
    options.set_argument("--disable-blink-features=AutomationControlled")
    options.set_argument("--autoplay-policy=no-user-gesture-required")
    options.set_argument("--mute-audio")
    options.set_argument("--window-position=-32000,-32000")
    options.set_argument("--window-size=1280,800")
    page = ChromiumPage(addr_or_opts=options)
    page._umos_profile = profile
    install_item_capture(page)
    return page


def close_browser(page):
    profile = getattr(page, "_umos_profile", "")
    attempt(page.quit)
    if profile:
        shutil.rmtree(profile, ignore_errors=True)


def browser_cookie(page):
    values = attempt(lambda: page.cookies(as_dict=True), {})
    return "; ".join(f"{key}={value}" for key, value in values.items())


def packet_result(packet, page, referer):
    url = str(getattr(packet, "url", "") or getattr(getattr(packet, "request", None), "url", "") or "")
    response_headers = dict(getattr(getattr(packet, "response", None), "headers", {}) or {})
    content_type = response_headers.get("Content-Type", response_headers.get("content-type", ""))
    if not is_media_url(url, content_type):
        return None
    request_headers = dict(getattr(getattr(packet, "request", None), "headers", {}) or {})
    user_agent = attempt(lambda: page.user_agent, USER_AGENT)
    result = stream_result(url, referer, user_agent, browser_cookie(page), request_headers)
    result['content_type'] = content_type
    return result


def start_listener(page):
    attempt(page.listen.stop)
    page.listen.start(targets=MEDIA_LISTEN_TARGETS)


def listen_for_media(page, referer, timeout):
    for packet in page.listen.steps(timeout=max(.25, timeout)):
        result = packet_result(packet, page, referer)
        if result:
            return result
    return None


DOM_SCAN_SCRIPT = r"""
return (() => {
    const media = new Set();
    const frames = new Set();
    const addMedia = value => {
        if (typeof value === 'string' && /\.(m3u8|mpd|mp4|mkv|webm)(\?|#|$)|mpegurl|dash\+xml/i.test(value)) media.add(value);
    };
    try { addMedia(window.__JW && window.__JW.state.hlsUrl); } catch (_) {}
    document.querySelectorAll('video,audio,source,[data-file],[data-url],[data-stream],[data-hls]').forEach(el => {
        ['src','data-src','data-file','data-url','data-stream','data-hls'].forEach(name => addMedia(el.getAttribute(name)));
        addMedia(el.currentSrc);
    });
    try { performance.getEntriesByType('resource').forEach(entry => addMedia(entry.name)); } catch (_) {}
    document.querySelectorAll('iframe,embed').forEach(el => { if (el.src) frames.add(el.src); });
    return { media: [...media], frames: [...frames] };
})();
"""


def browser_dom_scan(page, referer, rejected=None):
    rejected = rejected or set()
    scanned = attempt(lambda: page.run_js(DOM_SCAN_SCRIPT), {}) or {}
    user_agent = attempt(lambda: page.user_agent, USER_AGENT)
    for value in scanned.get("media", []):
        url = normalize_url(value, getattr(page, "url", "") or referer)
        if is_media_url(url) and url not in rejected:
            return stream_result(url, referer, user_agent, browser_cookie(page)), scanned.get("frames", [])
    for value in extract_media_urls(attempt(lambda: page.html, ""), getattr(page, "url", "") or referer):
        if value not in rejected:
            return stream_result(value, referer, user_agent, browser_cookie(page)), scanned.get("frames", [])
    return None, scanned.get("frames", [])


def click_player_controls(page, referer, deadline):
    selectors = (
        "css:[data-link-id]", "css:[data-server]", "css:.server", "css:.servers li",
        "css:#bigPlay", "css:button[aria-label='Play']", "css:.play-btn", "css:.play-button", "css:[class*='play']", "css:video",
    )
    clicked = set()
    for selector in selectors:
        if time.monotonic() >= deadline:
            break
        elements = attempt(lambda selector=selector: page.eles(selector, timeout=.1), []) or []
        for element in elements[:3]:
            marker = "|".join(str(attempt(lambda name=name: element.attr(name), "") or "") for name in (
                "data-link-id", "data-server", "data-type", "title", "aria-label",
            ))
            marker = (selector, marker)
            if marker in clicked:
                continue
            clicked.add(marker)
            start_listener(page)
            clicked_ok = attempt(lambda: (element.click(), True)[1], False)
            if not clicked_ok:
                attempt(lambda: element.click(by_js=True))
            result = attempt(lambda: listen_for_media(
                page, referer, min(.4, max(.1, deadline - time.monotonic()))))
            if result:
                return result
            result, _ = browser_dom_scan(page, referer)
            if result:
                return result
    return None


def scan_embedded_players(page, requested, deadline):
    # Inspect loaded frames in place. Navigating their signed URLs as top-level
    # pages loses parent/referrer context and can turn a playable iframe into 404.
    queue = deque([(page, 0)])
    seen = set()
    rejected = set()
    first_native = None
    while queue and time.monotonic() < deadline:
        document, depth = queue.popleft()
        current = getattr(document, 'url', '') or requested
        if current in seen or not compatible_player_page(current, requested):
            continue
        seen.add(current)
        if depth:
            shape = attempt(lambda: document.run_js('return [window.innerWidth,window.innerHeight]'), [])
            if len(shape) != 2 or shape[0] < 300 or shape[1] < 150:
                continue
        start_listener(document)
        for _ in range(3):
            candidate, _ = browser_dom_scan(document, current, rejected)
            if not candidate:
                break
            result = validated_stream(candidate, deadline)
            if result:
                result = enrich_native_result(result, document, requested, deadline)
                if result:
                    if result.get('evidence') or result.get('subtitles'):
                        return result
                    first_native = first_native or result
                    break
                if first_native and first_native['url'] == candidate['url']:
                    first_native = None
            rejected.add(candidate['url'])
        result = attempt(lambda: click_player_controls(document, current,
            min(deadline, time.monotonic() + 1.8)))
        if result and result['url'] not in rejected:
            control_url = result['url']
            result = validated_stream(result, deadline)
            if result:
                result = enrich_native_result(result, document, requested, deadline)
                if result:
                    if result.get('evidence') or result.get('subtitles'):
                        return result
                    first_native = first_native or result
                else:
                    rejected.add(control_url)
                    if first_native and first_native['url'] == control_url:
                        first_native = None
        if depth < 4:
            frames = attempt(lambda: document.get_frames(timeout=.15), []) or []
            queue.extend((frame, depth + 1) for frame in frames[:8])
    return first_native


def browser_target(page, url, deadline, requested=None):
    requested = requested or url
    start_listener(page)
    # A navigation timeout can still leave a usable loaded document to inspect.
    attempt(lambda: page.get(url, timeout=min(6.0, max(.1, deadline - time.monotonic()))))
    current = getattr(page, "url", "") or url
    if not compatible_player_page(current, requested):
        return None, [], None
    result = scan_embedded_players(page, requested, deadline)
    if result:
        return result, [], None
    _, frames = browser_dom_scan(page, current)
    page_html = attempt(lambda: page.html, "")
    frames = list(dict.fromkeys(frame for frame in frames + extract_child_pages(page_html, current)
        if is_player_page(frame) and compatible_player_page(frame, requested)))
    fallback = None
    if re.search(r"<(?:video|iframe|embed)\b|player|jwplayer|videojs", page_html, re.I):
        fallback = stream_result(
            current,
            current,
            attempt(lambda: page.user_agent, USER_AGENT),
            browser_cookie(page),
            requires_webview=True,
        )
    return None, frames, fallback


def browser_waterfall(targets, deadline):
    if time.monotonic() >= deadline:
        return None
    page = launch_browser()
    queue = deque((target, 0) for target in targets)
    visited = set()
    fallback = None
    try:
        while queue and time.monotonic() < deadline:
            url, depth = queue.popleft()
            if url in visited or depth > 4 or not compatible_player_page(url, targets[0]):
                continue
            visited.add(url)
            outcome = attempt(lambda url=url: browser_target(page, url, deadline, targets[0]))
            if not outcome:
                continue
            result, frames, candidate_fallback = outcome
            if result:
                return result
            fallback = fallback or candidate_fallback
            for frame in reversed(frames[:4]):
                child = normalize_url(frame, getattr(page, "url", "") or url)
                if child and child not in visited:
                    queue.appendleft((child, depth + 1))
        return fallback
    finally:
        close_browser(page)


def compatible_mirror_targets(url):
    parsed = urlparse(url)
    if "vidsrc" not in (parsed.hostname or "").lower():
        return []
    targets = []
    for mirror in VIDSRC_MIRRORS:
        root = urlparse(mirror)
        candidate = parsed._replace(scheme=root.scheme, netloc=root.netloc).geturl()
        if candidate != url and candidate not in targets:
            targets.append(candidate)
    return targets


def embed_alternate_targets(url):
    parsed = urlparse(url)
    path = parsed.path.rstrip("/")
    pairs = dict(parse_qsl(parsed.query, keep_blank_values=True))
    media_id = season = episode = ""
    kind = "movie"

    match = re.search(r"(?i)/embed/(?:movie/)?([A-Za-z0-9_-]+)$", path)
    if match:
        media_id = match.group(1)
    match = re.search(r"(?i)/embed/tv/([A-Za-z0-9_-]+)/(\d+)/(\d+)$", path)
    if match:
        media_id, season, episode = match.groups()
        kind = "television"
    if pairs.get("video_id"):
        media_id = pairs["video_id"]
        season = pairs.get("s", "")
        episode = pairs.get("e", "")
        kind = "television" if season and episode else "movie"
    match = re.search(r"(?i)/movie/(?:tmdb|imdb)/([A-Za-z0-9_-]+)$", path)
    if match:
        media_id = match.group(1)
    match = re.search(r"(?i)/tv/(?:tmdb|imdb)/([A-Za-z0-9_-]+)-(\d+)-(\d+)$", path)
    if match:
        media_id, season, episode = match.groups()
        kind = "television"
    if not media_id:
        return []

    catalog = "imdb" if media_id.lower().startswith("tt") else "tmdb"
    vidsrc_root = f"{parsed.scheme}://{parsed.netloc}" if "vidsrc" in (parsed.hostname or "").lower() else "https://vidsrc.to"
    candidates = [
        item["url"]
        for item in build_embed_results(catalog, media_id, kind, season, episode, vidsrc_root)
    ]
    return [candidate for candidate in candidates if candidate != url]


def do_extract(url, audio_preference="sub", budget_seconds=34, include_alternatives=True):
    budget = attempt(lambda: float(budget_seconds), 34.0)
    deadline = time.monotonic() + max(12.0, min(budget, 34.0))
    if is_media_url(url):
        return validated_stream(stream_result(url, url), deadline) or {"error": "media_validation_failed"}
    targets = []
    for target in [url, *(embed_alternate_targets(url) if include_alternatives else []), *compatible_mirror_targets(url)]:
        if target not in targets:
            targets.append(target)
    # Reserve most of the operation for the actual interactive player.
    result, browser_targets = static_waterfall(targets, min(deadline, time.monotonic() + 5))
    if result:
        return result
    if time.monotonic() >= deadline:
        return {"error": "all_sources_failed"}
    result = attempt(lambda: browser_waterfall(browser_targets, deadline))
    return result or {"error": "all_sources_failed"}


def main(argv):
    if len(argv) < 3:
        print(json.dumps({"error": "usage: audiovisual_scraper.py search|extract|resolve <argument>"}))
        return 1
    mode = argv[1].lower()
    argument = argv[2]
    if mode == "search":
        kind = argv[3] if len(argv) > 3 else "movie"
        year = argv[4] if len(argv) > 4 and argv[4] else None
        season = argv[5] if len(argv) > 5 and argv[5] else None
        episode = argv[6] if len(argv) > 6 and argv[6] else None
        mirror = argv[7] if len(argv) > 7 and argv[7] else "https://vidsrc.to"
        output = do_search(argument, kind, year, season, episode, mirror)
    elif mode == "extract":
        audio = argv[3] if len(argv) > 3 else "sub"
        budget = argv[4] if len(argv) > 4 else 34
        output = do_extract(argument, audio, budget)
    elif mode == "resolve":
        budget = argv[3] if len(argv) > 3 else 34
        # The C# source coordinator already queues the other providers. Keep this
        # candidate's own mirrors without crawling the same alternatives twice.
        output = do_extract(argument, "sub", budget, include_alternatives=False)
    else:
        output = {"error": f"unknown mode: {mode}"}
    print(json.dumps(output, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
