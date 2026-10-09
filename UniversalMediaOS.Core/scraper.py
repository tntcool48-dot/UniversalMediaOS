#!/usr/bin/env python3
"""
UniversalMediaOS Bulletproof Scraper v2.0
Stateless CLI: python scraper.py search "Title" | extract "url"
Outputs strict JSON to stdout. All logging to stderr.
"""
import sys, os, re, json, base64, time, traceback, html as html_lib, tempfile, shutil, socket, ctypes, threading, ipaddress
from concurrent.futures import ThreadPoolExecutor, as_completed
from html.parser import HTMLParser
from urllib.parse import quote_plus, unquote, urljoin, urlparse, parse_qsl, urlencode, urlsplit, urlunsplit

try:
    import curl_cffi.requests as cffi_req
except ImportError:
    cffi_req = None

try:
    from DrissionPage import ChromiumPage, ChromiumOptions
    DRISSION_AVAILABLE = True
except ImportError:
    DRISSION_AVAILABLE = False

TIMEOUT_PER_MIRROR = 12
DYNAMIC_PLAYER_WAIT = 12
NETWORK_SNIFF_SECONDS = 10
PLAYER_MEDIA_WAIT = 20
SEARCH_CANDIDATE_TIMEOUT = 3.5
SITE_SEARCH_URL_LIMIT = 6
DEFAULT_RESOLVE_BUDGET_SECONDS = 75
SCRAPER_PROFILE_MAX_AGE_HOURS = 12
RESOLVE_SITE_BUDGET_SECONDS = 24
RESOLVE_CANDIDATE_BUDGET_SECONDS = 18
INDEX_CACHE_MAX_AGE_SECONDS = 6 * 60 * 60
SEARCH_GLOBAL_BUDGET_SECONDS = 20
SEARCH_MAX_SITES = 12
SEARCH_MAX_WORKERS = 6

DATA_ROOT = os.environ.get("UNIVERSAL_MEDIA_OS_DATA_ROOT", "").strip()
if DATA_ROOT and not os.path.isabs(DATA_ROOT):
    raise ValueError("UNIVERSAL_MEDIA_OS_DATA_ROOT must be an absolute path")
PROFILE_ROOT = os.path.join(DATA_ROOT, "Local", "UniversalMediaOS", "BrowserProfiles") if DATA_ROOT else tempfile.gettempdir()

SCRAPER_CACHE_DIR = os.path.join(
    os.path.join(DATA_ROOT, "Local") if DATA_ROOT else (os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()),
    "UniversalMediaOS",
    "Cache")
INDEX_CACHE_PATH = os.path.join(SCRAPER_CACHE_DIR, "streaming-sites.json")

HIGH_CONFIDENCE_SITE_BONUS = (
    ("anikoto", 80),
    ("miruro", 70),
    ("anidb", 65),
    ("reanime", 40),
    ("anitaku", 20),
    ("gogoanime", 20),
    ("animepahe", 15),
)

MEDIA_LISTEN_TARGETS = [
    "m3u8", ".m3u8", "mpegurl", "playlist", "manifest", ".vtt", ".srt", "getSources", "/sources"
]

MEDIA_URL_RE = re.compile(r'https?://[^\s"\'<>\\]+\.(?:m3u8|mp4)(?:[^\s"\'<>\\]*)?', re.I)
FORBIDDEN_REPLAY_HEADERS = {
    "host", "connection", "content-length", "transfer-encoding", "te", "trailer",
    "upgrade", "proxy-connection", "proxy-authenticate", "proxy-authorization",
    "accept-encoding", "range"
}
IFRAME_SKIP_RE = re.compile(
    r"(doubleclick|googlesyndication|google-analytics|google\.com/recaptcha|captcha|"
    r"adservice|adsystem|adtrafficquality|analytics|facebook|twitter|about:blank|javascript:)",
    re.I)

BLOCKED_SITE_KEYWORDS = (
    "youtube", "crunchyroll", "netflix", "hidive", "bilibili",
    "discord", "github.com", "boards.4chan", "vpn", "manga"
)

LOG_URL_RE = re.compile(r"https?://[^\s'\"<>]+", re.I)
LOG_URL_TRAILING_PUNCTUATION = ")]},;.!"
LOG_HEX_TOKEN_RE = re.compile(r"^[a-f0-9]{32,}$", re.I)
LOG_BASE64URL_TOKEN_RE = re.compile(r"^[a-z0-9_-]{40,}$", re.I)


def redact_log_urls(message):
    """Retain useful URL routes without leaking signed queries or path tokens."""
    def redact_path(path):
        segments = path.split("/")
        for index, raw_segment in enumerate(segments):
            try:
                segment = unquote(raw_segment)
            except (TypeError, ValueError):
                segment = raw_segment
            looks_hex = bool(LOG_HEX_TOKEN_RE.fullmatch(segment))
            looks_base64url = (
                bool(LOG_BASE64URL_TOKEN_RE.fullmatch(segment))
                and any(char.islower() for char in segment)
                and any(char.isupper() for char in segment)
                and any(char.isdigit() for char in segment)
                and len(set(segment)) >= 16
            )
            if looks_hex or looks_base64url:
                segments[index] = "<redacted>"
        return "/".join(segments)

    def redact_malformed(value):
        positions = [pos for pos in (value.find("?"), value.find("#")) if pos >= 0]
        sensitive_at = min(positions) if positions else -1
        marker = value[sensitive_at] if sensitive_at >= 0 else ""
        visible = value[:sensitive_at] if sensitive_at >= 0 else value
        scheme_end = visible.find("://")
        if scheme_end >= 0:
            authority_start = scheme_end + 3
            authority_end = visible.find("/", authority_start)
            if authority_end < 0:
                authority_end = len(visible)
            at = visible.rfind("@", authority_start, authority_end)
            if at >= authority_start:
                visible = visible[:authority_start] + visible[at + 1:]
        return visible + (f"{marker}<redacted>" if marker else "")

    def replace(match):
        candidate = match.group(0)
        content = candidate.rstrip(LOG_URL_TRAILING_PUNCTUATION)
        suffix = candidate[len(content):]
        try:
            parts = urlsplit(content)
            if parts.scheme.lower() not in ("http", "https") or not parts.hostname:
                return redact_malformed(content) + suffix
            host = f"[{parts.hostname}]" if ":" in parts.hostname else parts.hostname
            if parts.port is not None:
                host = f"{host}:{parts.port}"
            query = "<redacted>" if parts.query else ""
            fragment = "<redacted>" if parts.fragment else ""
            return urlunsplit((parts.scheme, host, redact_path(parts.path), query, fragment)) + suffix
        except (TypeError, ValueError):
            return redact_malformed(content) + suffix

    return LOG_URL_RE.sub(replace, str(message))


def log(message):
    print(f"[scraper] {redact_log_urls(message)}", file=sys.stderr, flush=True)


def deadline_expired(deadline):
    return deadline is not None and time.monotonic() >= deadline


def seconds_left(deadline, default_seconds, minimum=0.5):
    if deadline is None:
        return default_seconds
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        return minimum
    return max(minimum, min(default_seconds, remaining))


def combine_deadlines(*deadlines):
    values = [d for d in deadlines if d is not None]
    return min(values) if values else None


def has_header(headers, name):
    wanted = name.lower()
    return any(str(key).lower() == wanted for key in headers)


def set_header_if_missing(headers, name, value):
    if value and not has_header(headers, name):
        headers[name] = str(value)


def sanitize_request_headers(headers):
    sanitized = {}
    if not headers:
        return sanitized
    try:
        items = headers.items() if hasattr(headers, "items") else dict(headers).items()
    except Exception:
        return sanitized
    for key, value in items:
        if key is None or value is None:
            continue
        name = str(key).strip()
        lower = name.lower()
        if not name or lower.startswith(":") or lower in FORBIDDEN_REPLAY_HEADERS:
            continue
        if isinstance(value, (list, tuple)):
            value = ", ".join(str(v) for v in value if v is not None)
        value = str(value).strip()
        if not value:
            continue
        sanitized[name] = value[:8192]
    return sanitized


def extract_packet_request_headers(packet):
    try:
        request = getattr(packet, "request", None)
        headers = getattr(request, "headers", None) if request else None
        return sanitize_request_headers(headers)
    except Exception:
        return {}


def build_replay_headers(result):
    headers = sanitize_request_headers(result.get("headers") or {})
    set_header_if_missing(headers, "User-Agent", result.get("user_agent") or "Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
    set_header_if_missing(headers, "Accept", "*/*")
    headers["Accept-Encoding"] = "identity"
    if result.get("referer"):
        set_header_if_missing(headers, "Referer", result["referer"])
        parsed_referer = urlparse(result["referer"])
        if parsed_referer.scheme and parsed_referer.netloc:
            set_header_if_missing(headers, "Origin", f"{parsed_referer.scheme}://{parsed_referer.netloc}")
    if result.get("cookie"):
        set_header_if_missing(headers, "Cookie", result["cookie"])
    return headers


def fetch_text(url, timeout=10):
    headers = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
    if cffi_req:
        r = cffi_req.get(url, impersonate="chrome120", timeout=timeout, headers=headers)
        return r.status_code, getattr(r, "url", url), r.text
    import urllib.request
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.status, resp.geturl(), resp.read().decode("utf-8", errors="ignore")


def normalize_root(url):
    parsed = urlparse(url)
    if not parsed.scheme or not parsed.netloc:
        return url.rstrip("/")
    return f"{parsed.scheme}://{parsed.netloc}".rstrip("/")


def is_safe_public_url(url):
    """Reject local/file targets before an index entry reaches the HTTP client or browser."""
    try:
        parsed = urlparse(str(url or ""))
        if parsed.scheme.lower() not in ("http", "https") or not parsed.hostname:
            return False
        if parsed.username or parsed.password:
            return False
        host = parsed.hostname.rstrip(".").lower()
        if host == "localhost" or host.endswith((".localhost", ".local", ".internal")):
            return False
        if re.fullmatch(r"(?:0x[0-9a-f]+|\d+)", host, re.I):
            # Reject alternate numeric spellings such as 2130706433 and 0x7f000001,
            # which operating systems may interpret as loopback addresses.
            return False
        try:
            address = ipaddress.ip_address(host)
            if not address.is_global:
                return False
        except ValueError:
            pass
        return True
    except Exception:
        return False


def site_key(url):
    parsed = urlparse(url)
    host = (parsed.netloc or url).lower()
    return host[4:] if host.startswith("www.") else host


def should_skip_site(name, url):
    haystack = f"{name} {url}".lower()
    return (not is_safe_public_url(url)) or any(blocked in haystack for blocked in BLOCKED_SITE_KEYWORDS)


def preferred_site_bonus(name, url):
    haystack = f"{name} {url}".lower()
    for keyword, bonus in HIGH_CONFIDENCE_SITE_BONUS:
        if keyword in haystack:
            return bonus
    return 0


def normalize_title(text):
    return re.sub(r"[^a-z0-9\s]", " ", (text or "").lower()).strip()


def query_keywords(query):
    words = re.findall(r"[a-z0-9]+", normalize_title(query))
    ignored = {"dub", "sub", "eng", "english", "dubbed", "season", "the", "and", "of"}
    useful = [word for word in words if word not in ignored and (len(word) >= 3 or any(ch.isdigit() for ch in word))]
    # Short titles such as "86" and "K-On!" have no 3-character token. Falling
    # back to their real short tokens is safer than treating every page as relevant.
    return useful or [word for word in words if word not in ignored]


def safe_title_aliases(query, aliases=None):
    """Use only short, distinct catalog titles for the same requested entry."""
    titles = [query]
    if not isinstance(aliases, (list, tuple)):
        return titles
    for alias in aliases[:2]:
        if not isinstance(alias, str) or len(alias) > 140:
            continue
        alias = alias.strip()
        if not alias or normalize_title(alias) in {normalize_title(title) for title in titles}:
            continue
        if len(set(query_keywords(alias))) < 2:
            continue  # A generic one-word synonym is not enough to identify a show.
        if variant_conflicts(alias, query) or variant_conflicts(query, alias):
            continue  # Never transfer another season, part, movie or special.
        titles.append(alias)
    return titles


def safe_catalog_ids(ids):
    """Only positive catalog IDs can participate in provider-page comparison."""
    if not isinstance(ids, dict):
        return {}
    result = {}
    for name in ("anilist", "mal"):
        raw = ids.get(name)
        if isinstance(raw, bool) or not re.fullmatch(r"[1-9]\d{0,9}", str(raw or "")):
            continue
        result[name] = int(raw)
    return result


def safe_catalog_synonym(query, trusted_titles, synonyms, catalog_ids):
    """One catalog synonym may rescue a miss, but its page must prove identity."""
    if len(trusted_titles) != 1 or not safe_catalog_ids(catalog_ids) or not isinstance(synonyms, (list, tuple)):
        return None
    options = []
    primary_words = set(query_keywords(query))
    for index, raw in enumerate(synonyms[:8]):
        if not isinstance(raw, str) or len(raw) > 140:
            continue
        title = raw.strip()
        words = set(query_keywords(title))
        if not title.isascii() or len(words) < 2 or normalize_title(title) == normalize_title(query):
            continue
        if variant_conflicts(title, query) or variant_conflicts(query, title):
            continue
        options.append((-len(primary_words & words), index, title))
    return min(options)[2] if options else None


def match_tokens(text):
    return set(re.findall(r"[a-z0-9]+", normalize_title(text)))


def matched_query_keywords(text, keywords):
    tokens = match_tokens(text)
    return {word for word in keywords if word in tokens}


def normalize_audio_preference(value):
    return "dub" if str(value or "").strip().lower() in ("dub", "dubbed", "eng", "english") else "sub"


def audio_score(text, audio_preference):
    audio = normalize_audio_preference(audio_preference)
    haystack = (text or "").lower()
    if audio == "dub":
        score = 0
        if re.search(r"\b(dub|dubbed|english|eng|dual[-\s]?audio|multi[-\s]?audio)\b", haystack, re.I):
            score += 20
        if re.search(r"\b(sub|subbed|subtitle|subtitles)\b", haystack, re.I):
            score -= 18
        return score

    score = 0
    if re.search(r"\b(sub|subbed|subtitle|subtitles)\b", haystack, re.I):
        score += 8
    if re.search(r"\b(dub|dubbed)\b", haystack, re.I):
        score -= 4
    return score


def prefer_audio_player_url(url, audio_preference):
    if not url:
        return url

    audio = normalize_audio_preference(audio_preference)
    desired = "dub" if audio == "dub" else "sub"
    other = "sub" if audio == "dub" else "dub"
    rewritten = str(url)

    # Many player iframes expose the same opaque id through /sub and /dub routes.
    rewritten = re.sub(rf"(?i)(/){other}(?=([/?#&]|$))", rf"\1{desired}", rewritten)
    rewritten = re.sub(rf"(?i)([?&](?:server|type|audio|lang)=){other}\b", rf"\1{desired}", rewritten)
    rewritten = re.sub(rf"(?i)([?&](?:server|type|audio|lang)=){other}bed\b", rf"\1{desired}", rewritten)
    return rewritten


def expand_audio_url_candidates(url, audio_preference):
    candidates = []

    def add(candidate):
        if candidate and candidate not in candidates:
            candidates.append(candidate)

    preferred = prefer_audio_player_url(url, audio_preference)
    add(preferred)
    add(url)
    return candidates


def html_is_relevant(text, query, title_aliases=None):
    for title in safe_title_aliases(query, title_aliases):
        keywords = query_keywords(title)
        if keywords and len(matched_query_keywords(text, keywords)) >= min(2, len(keywords)):
            return True
    return False


def add_site(pool, name, url, source, score, rank=999, tags=None):
    if not url or not url.startswith("http"):
        return
    if should_skip_site(name, url):
        return
    pool.append({
        "name": html_lib.unescape(name or site_key(url)).strip(),
        "url": html_lib.unescape(url).strip().rstrip("/"),
        "source": source,
        "score": int(score) + preferred_site_bonus(name, url),
        "rank": int(rank),
        "tags": tags or [],
    })

# ---- Index fetcher ------------------------------------------------------------

def load_index_cache(allow_stale=False):
    try:
        if not os.path.isfile(INDEX_CACHE_PATH):
            return []
        age = max(0, time.time() - os.path.getmtime(INDEX_CACHE_PATH))
        if not allow_stale and age > INDEX_CACHE_MAX_AGE_SECONDS:
            return []
        with open(INDEX_CACHE_PATH, "r", encoding="utf-8") as stream:
            cached = json.load(stream)
        if not isinstance(cached, list):
            return []
        # Earlier caches mixed index entries with fixed seeds. Only reuse sites
        # actually discovered from an index; retired seeds must not return offline.
        safe = [item for item in cached if isinstance(item, dict)
                and item.get("source") in ("everythingmoe", "theindex")
                and not should_skip_site(item.get("name", ""), item.get("url", ""))]
        if safe:
            freshness = "stale fallback" if age > INDEX_CACHE_MAX_AGE_SECONDS else "fresh"
            log(f"Loaded {len(safe)} indexed sites from {freshness} cache ({age / 60:.0f}m old)")
        return safe
    except Exception as e:
        log(f"Streaming-site cache read failed: {e}")
        return []


def save_index_cache(sites):
    if not sites:
        return
    temp_path = f"{INDEX_CACHE_PATH}.{os.getpid()}.tmp"
    try:
        os.makedirs(SCRAPER_CACHE_DIR, exist_ok=True)
        with open(temp_path, "w", encoding="utf-8") as stream:
            json.dump(sites, stream, ensure_ascii=False)
        os.replace(temp_path, INDEX_CACHE_PATH)
    except Exception as e:
        log(f"Streaming-site cache write failed: {e}")
        try:
            if os.path.exists(temp_path):
                os.remove(temp_path)
        except Exception:
            pass


def fetch_mirror_pool():
    return [site["url"] for site in fetch_indexed_sites()]


def fetch_indexed_sites():
    cached = load_index_cache()
    if cached:
        return cached

    pool = []
    log("Fetching streaming site indexes: EverythingMoe + The Index")
    index_requests = (
        ("EverythingMoe", "https://everythingmoe.com/section/anime", parse_everythingmoe),
        ("TheIndex", "https://theindex.moe/items", parse_theindex),
    )
    with ThreadPoolExecutor(max_workers=2) as executor:
        futures = {
            executor.submit(fetch_text, url, 12): (name, parser)
            for name, url, parser in index_requests
        }
        for future in as_completed(futures):
            name, parser = futures[future]
            try:
                status, _, text = future.result()
                log(f"{name} status={status}, bytes={len(text)}")
                if status == 200:
                    parser(text, pool)
            except Exception as e:
                log(f"{name} fetch failed: {e}")

    merged = {}
    for item in pool:
        key = site_key(item["url"])
        current = merged.get(key)
        if current is None or item["score"] > current["score"]:
            merged[key] = item

    ranked = sorted(merged.values(), key=lambda s: (-s["score"], s["rank"], s["name"].lower()))
    if ranked:
        save_index_cache(ranked)
    else:
        ranked = load_index_cache(allow_stale=True) or ranked
    if not ranked:
        log("No streaming sites are available from the indexes or their saved list. Retry when an index is reachable.")
    log(f"Indexed usable sites={len(ranked)}; top={', '.join(s['name'] for s in ranked[:8])}")
    return ranked


def parse_everythingmoe(text, pool):
    pattern = re.compile(
        r'<div data-rank="(?P<rank>\d+)" data-filter="(?P<tags>[^"]*)" class="section-item">.*?'
        r'<a href="[^"]+" data-link="(?P<url>[^"]+)">.*?alt="">\s*(?P<name>[^<]+)</a>',
        re.S | re.I)
    count = 0
    for match in pattern.finditer(text):
        rank = int(match.group("rank"))
        tags = [t.strip() for t in match.group("tags").split(",") if t.strip()]
        tags_lower = {t.lower() for t in tags}
        score = 250 - rank * 3
        if "scraper" in tags_lower:
            score += 35
        if "self-host" in tags_lower:
            score += 25
        if "modern interface" in tags_lower:
            score += 12
        if "soft-sub" in tags_lower:
            score += 8
        if "dub friendly" in tags_lower:
            score += 6
        if "easy download" in tags_lower:
            score += 4
        if "third party" in tags_lower:
            score -= 20
        add_site(pool, match.group("name"), match.group("url"), "everythingmoe", score, rank, tags)
        count += 1
    log(f"EverythingMoe parsed entries={count}")


def parse_theindex(text, pool):
    match = re.search(r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>', text, re.S)
    if not match:
        log("TheIndex __NEXT_DATA__ block not found")
        return

    data = json.loads(html_lib.unescape(match.group(1)))
    page_props = data.get("props", {}).get("pageProps", {})
    columns = {c.get("_id"): c.get("urlId") for c in page_props.get("columns", [])}
    count = 0
    for item in page_props.get("items", []):
        if item.get("nsfw") or item.get("blacklist") or item.get("sponsor"):
            continue
        urls = item.get("urls") or []
        if not urls:
            continue
        feature_data = {columns.get(k, k): v for k, v in (item.get("data") or {}).items()}
        if not looks_like_streaming_item(item, feature_data):
            continue
        score = 60
        ads = feature_data.get("ads")
        anti_adblock = feature_data.get("anti-adblock")
        if ads is False:
            score += 25
        elif ads is True:
            score -= 20
        if anti_adblock is False:
            score += 18
        elif anti_adblock is True:
            score -= 12
        if feature_data.get("mobile"):
            score += 6
        if feature_data.get("dl"):
            score += 4
        if feature_data.get("mtl") is True:
            score -= 8
        quality = feature_data.get("360p") or []
        if isinstance(quality, list):
            if "1080p" in quality:
                score += 10
            if "720p" in quality:
                score += 5
        for lang_key in ("subs", "dubs", "languages"):
            values = feature_data.get(lang_key) or []
            if isinstance(values, list) and "eng" in values:
                score += 5
        for url in urls:
            add_site(pool, item.get("name", ""), url, "theindex", score, rank=500, tags=list(feature_data.keys()))
            count += 1
    log(f"TheIndex parsed streaming-like entries={count}")


def looks_like_streaming_item(item, feature_data):
    haystack = f"{item.get('name','')} {' '.join(item.get('urls') or [])} {item.get('description','')}".lower()
    if any(k in haystack for k in ("anime", "ani", "kissa", "miruro", "otaku", "zoro", "hianime", "gogo", "pahe", "stream")):
        return True
    return any(k in feature_data for k in ("subs", "dubs", "360p", "list-sync")) and not item.get("nsfw")

# ---- Stealth scripts ----------------------------------------------------------

STEALTH_SCRIPT = """
(function() {
    try { delete window.__playwright__binding__; } catch(e) {}
    try { delete window.__pw_manual; } catch(e) {}
    Object.defineProperty(navigator, 'webdriver', { get: () => false, configurable: false });
    const _fetch = window.fetch;
    const _pf = function(...args) { return _fetch.apply(this, args); };
    Object.defineProperty(window, 'fetch', { value: _pf, writable: false });
    _pf.toString = () => 'function fetch() { [native code] }';
    const _open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(...args) { return _open.apply(this, args); };
    XMLHttpRequest.prototype.open.toString = () => 'function open() { [native code] }';
})();
"""

WORKER_INTERCEPT = """
(function() {
    const _fetch = self.fetch;
    self._ums_captured = [];
    const pf = function(...args) {
        const url = typeof args[0] === 'string' ? args[0] : (args[0] && args[0].url ? args[0].url : '');
        if (url.includes('.m3u8') || url.includes('playlist') || url.includes('manifest')) {
            self._ums_captured.push(url);
        }
        return _fetch.apply(this, args);
    };
    Object.defineProperty(self, 'fetch', { value: pf, writable: false });
})();
"""

# ---- Browser utilities -------------------------------------------------------

def cleanup_old_scraper_profiles(max_age_hours=SCRAPER_PROFILE_MAX_AGE_HOURS):
    try:
        root = PROFILE_ROOT
        cutoff = time.time() - max_age_hours * 3600
        removed = 0
        for name in os.listdir(root):
            if not name.startswith("umos_scraper_"):
                continue
            path = os.path.join(root, name)
            if not os.path.isdir(path):
                continue
            try:
                if os.path.getmtime(path) < cutoff:
                    shutil.rmtree(path, ignore_errors=True)
                    removed += 1
            except Exception:
                pass
        if removed:
            log(f"Cleaned up stale scraper profiles: {removed}")
    except Exception as e:
        log(f"Stale scraper profile cleanup failed: {e}")


def hide_browser_windows(page, log_success=True):
    """Hide Chromium's top-level windows on Windows while keeping headful rendering."""
    if os.name != "nt":
        return 0

    process_id = int(getattr(page, "process_id", 0) or 0)
    if process_id <= 0:
        return 0

    try:
        user32 = ctypes.windll.user32
        GWL_EXSTYLE = -20
        WS_EX_TOOLWINDOW = 0x00000080
        WS_EX_APPWINDOW = 0x00040000
        SW_HIDE = 0

        hidden_count = 0

        @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        def enum_windows(hwnd, _):
            nonlocal hidden_count
            window_pid = ctypes.c_ulong()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(window_pid))
            if window_pid.value == process_id:
                ex_style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
                ex_style = (ex_style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW
                user32.SetWindowLongW(hwnd, GWL_EXSTYLE, ex_style)
                user32.ShowWindow(hwnd, SW_HIDE)
                hidden_count += 1
            return True

        for _ in range(20):
            hidden_count = 0
            user32.EnumWindows(enum_windows, 0)
            if hidden_count:
                if log_success:
                    log(f"Browser windows hidden for scraper process pid={process_id} windows={hidden_count}")
                return hidden_count
            time.sleep(0.1)
    except Exception as e:
        log(f"Browser window hide failed; continuing off-screen: {e}")
    return 0


def start_browser_hide_monitor(page):
    if os.name != "nt":
        return None
    stop_event = threading.Event()

    def monitor():
        while not stop_event.wait(0.75):
            try:
                hide_browser_windows(page, log_success=False)
            except Exception:
                pass

    thread = threading.Thread(target=monitor, name="umos-browser-hide", daemon=True)
    thread.start()
    return stop_event

def launch_browser():
    if not DRISSION_AVAILABLE:
        print("[scraper] DrissionPage not available.", file=sys.stderr)
        return None
    cleanup_old_scraper_profiles()
    os.makedirs(PROFILE_ROOT, exist_ok=True)
    profile_dir = tempfile.mkdtemp(prefix="umos_scraper_", dir=PROFILE_ROOT)
    try:
        opts = ChromiumOptions()
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
                sock.bind(("127.0.0.1", 0))
                local_port = sock.getsockname()[1]
            opts.set_local_port(local_port)
            opts.new_env(True)
            opts.set_user_data_path(profile_dir)
            opts.set_tmp_path(profile_dir)
        except Exception as e:
            log(f"Browser isolation option failed, continuing: {e}")
        opts.set_argument("--disable-blink-features=AutomationControlled")
        opts.set_argument("--disable-dev-shm-usage")
        opts.set_argument("--mute-audio")
        opts.set_argument("--autoplay-policy=no-user-gesture-required")
        opts.set_argument("--disable-background-networking")
        opts.set_argument("--disable-gpu")
        opts.set_argument("--window-position=-32000,-32000")
        opts.set_argument("--window-size=1200,800")
        page = ChromiumPage(addr_or_opts=opts)
        hide_browser_windows(page)
        hide_stop = start_browser_hide_monitor(page)
        try:
            page._ums_tmp_dir = profile_dir
        except Exception:
            pass
        try:
            page._ums_hide_stop = hide_stop
        except Exception:
            pass
        try:
            page.driver.run_cdp("Page.addScriptToEvaluateOnNewDocument", source=STEALTH_SCRIPT)
        except Exception:
            pass
        try:
            page.driver.run_cdp("Runtime.disable")
        except Exception:
            pass
        return page
    except Exception as e:
        shutil.rmtree(profile_dir, ignore_errors=True)
        print(f"[scraper] Browser launch failed: {e}", file=sys.stderr)
        return None


def close_browser(page):
    tmp_dir = getattr(page, "_ums_tmp_dir", None)
    hide_stop = getattr(page, "_ums_hide_stop", None)
    if hide_stop:
        try:
            hide_stop.set()
        except Exception:
            pass
    try:
        page.quit()
    except Exception:
        pass
    if tmp_dir:
        for _ in range(3):
            shutil.rmtree(tmp_dir, ignore_errors=True)
            if not os.path.exists(tmp_dir):
                break
            time.sleep(0.2)


def setup_worker_hooks(page):
    try:
        page.driver.run_cdp("Target.setAutoAttach",
                            autoAttach=True, waitForDebuggerOnStart=True, flatten=True)
    except Exception:
        pass

    def on_attached(event):
        try:
            info = event.get("params", {})
            session_id = info.get("sessionId", "")
            target_type = info.get("targetInfo", {}).get("type", "")
            if target_type in ("worker", "service_worker", "shared_worker") and session_id:
                page.driver.run_cdp("Runtime.enable", sessionId=session_id)
                page.driver.run_cdp("Runtime.evaluate",
                                    expression=WORKER_INTERCEPT, sessionId=session_id)
                page.driver.run_cdp("Runtime.runIfWaitingForDebugger", sessionId=session_id)
        except Exception:
            pass

    try:
        page.driver.set_callback("Target.attachedToTarget", on_attached)
    except Exception:
        pass


def get_cookies_str(page):
    try:
        cookies = page.cookies(as_dict=True)
        return "; ".join(f"{k}={v}" for k, v in cookies.items())
    except Exception:
        return ""


def get_ua(page):
    try:
        return page.user_agent
    except Exception:
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"


def make_stream_result(url, page, referer=None, request_headers=None):
    result = {
        "url": url,
        "user_agent": get_ua(page),
        "cookie": get_cookies_str(page),
        "referer": referer or getattr(page, "url", "") or ""
    }
    headers = sanitize_request_headers(request_headers)
    if headers:
        result["headers"] = headers
    tracks = collect_subtitle_tracks(page, headers)
    if tracks:
        result["subtitles"] = tracks
    selected_audio = getattr(page, "_ums_selected_audio", None)
    if selected_audio in ("sub", "dub"):
        result["selected_audio"] = selected_audio
    return result


def subtitle_track(item, page, base_url, headers=None):
    if not isinstance(item, dict):
        return None
    kind = str(item.get("kind") or "subtitles").lower()
    if kind not in ("subtitles", "captions"):
        return None
    url = normalize_absolute_url(item.get("file") or item.get("src") or item.get("url"), base_url)
    if not url or not is_safe_public_url(url) or urlparse(url).path.lower().endswith(".m3u8"):
        return None  # HLS subtitle groups stay on the master playlist.
    captured_headers = sanitize_request_headers(headers)
    same_origin = urlparse(url)[:2] == urlparse(base_url)[:2]
    captured_cookie = next((value for name, value in captured_headers.items() if name.lower() == "cookie"), "")
    return {
        "url": url,
        "label": str(item.get("label") or item.get("language") or item.get("srclang") or "Captions")[:80],
        "language": str(item.get("language") or item.get("srclang") or "")[:20],
        "default": item.get("default") is True,
        "referer": base_url if same_origin else urljoin(base_url, '/'),
        "user_agent": get_ua(page),
        "cookie": captured_cookie or (get_cookies_str(page) if same_origin else ""),
        "headers": captured_headers,
    }


def config_subtitle_tracks(config, page, base_url, headers=None, depth=0):
    if depth > 5:
        return []
    tracks = []
    if isinstance(config, dict):
        for key, value in config.items():
            if key.lower() in ("tracks", "subtitles", "captions") and isinstance(value, list):
                for item in value[:12]:
                    track = subtitle_track(item, page, base_url, headers)
                    if track:
                        tracks.append(track)
            elif isinstance(value, (dict, list)):
                tracks.extend(config_subtitle_tracks(value, page, base_url, headers, depth + 1))
    elif isinstance(config, list):
        for value in config[:12]:
            tracks.extend(config_subtitle_tracks(value, page, base_url, headers, depth + 1))
    return tracks[:12]


def collect_subtitle_tracks(page, media_headers=None):
    base_url = getattr(page, "url", "") or ""
    tracks = list(getattr(page, "_ums_subtitle_tracks", []) or [])
    try:
        items = page.run_js("""
            return (() => {
                const tracks = [];
                document.querySelectorAll('video track').forEach(el => tracks.push({
                    src: el.src, kind: el.kind, srclang: el.srclang, label: el.label, default: el.default
                }));
                try {
                    const item = window.jwplayer && jwplayer().getPlaylistItem();
                    if (item && Array.isArray(item.tracks)) tracks.push(...item.tracks);
                } catch (_) {}
                try {
                    if (window.videojs) Object.values(videojs.getPlayers()).forEach(player => {
                        const list = player.remoteTextTracks();
                        for (let i = 0; i < list.length; i++) {
                            const el = player.remoteTextTrackEls()[i];
                            if (el) tracks.push({src: el.src, kind: list[i].kind,
                                srclang: list[i].language, label: list[i].label});
                        }
                    });
                } catch (_) {}
                return tracks;
            })();
        """)
        if isinstance(items, list):
            for item in items[:12]:
                track = subtitle_track(item, page, base_url)
                if track:
                    tracks.append(track)
    except Exception as e:
        log(f"  Caption scan failed: {e}")
    # Network-captured headers take precedence over DOM guesses for the same file.
    merged = {}
    for track in tracks:
        previous = merged.get(track["url"])
        if previous is None:
            merged[track["url"]] = track
        else:
            if not previous.get("language"):
                previous["language"] = track.get("language", "")
            if previous.get("label") == "Captions":
                previous["label"] = track.get("label", "Captions")
            previous["default"] = previous.get("default") or track.get("default")
    # DOM/config tracks may be known before their first network request. Use
    # only the player's captured public request context as a fallback; caption
    # cookies and tokens must come from that caption's own request.
    public_context = {name: value for name, value in (media_headers or {}).items()
        if name.lower() in ("referer", "origin")}
    for track in merged.values():
        own_headers = track.get("headers") or {}
        combined = dict(own_headers)
        for name, value in public_context.items():
            set_header_if_missing(combined, name, value)
        track["headers"] = combined
        if not has_header(own_headers, "Referer"):
            track["referer"] = next((value for name, value in public_context.items()
                if name.lower() == "referer"), track.get("referer", ""))
    return list(merged.values())[:12]


def audio_languages_from_payload(data):
    """Inspect bounded media metadata; a page's Dub label is not audio evidence."""
    sections = {}
    for start in range(min(188, len(data))):
        if start + 188 * 3 >= len(data) or any(data[start + 188 * i] != 0x47 for i in range(4)):
            continue
        for offset in range(start, len(data) - 187, 188):
            packet = data[offset:offset + 188]
            if packet[0] != 0x47 or packet[1] & 0x80:
                continue
            pid = ((packet[1] & 31) << 8) | packet[2]
            adaptation = (packet[3] >> 4) & 3
            if adaptation not in (1, 3):
                continue
            cursor = 4 + (packet[4] + 1 if adaptation == 3 else 0)
            if cursor >= 188:
                continue
            if packet[1] & 0x40:
                cursor += 1 + packet[cursor]
                sections[pid] = bytearray()
            if pid not in sections or cursor >= 188:
                continue
            section = sections[pid]
            section.extend(packet[cursor:])
            if len(section) < 12 or section[0] != 2:
                continue
            end = 3 + (((section[1] & 15) << 8) | section[2]) - 4
            if end < 12 or end + 4 > len(section):
                continue
            languages = []
            stream = 12 + (((section[10] & 15) << 8) | section[11])
            while stream + 5 <= end:
                stream_type = section[stream]
                descriptor = stream + 5
                next_stream = descriptor + (((section[stream + 3] & 15) << 8) | section[stream + 4])
                if next_stream > end:
                    return []
                tags, stream_languages = [], []
                while descriptor + 2 <= next_stream:
                    tag, size = section[descriptor:descriptor + 2]
                    value = descriptor + 2
                    if value + size > next_stream:
                        return []
                    tags.append(tag)
                    if tag == 0x0a:
                        for index in range(value, value + size - 3, 4):
                            language = bytes(section[index:index + 3]).decode("ascii", errors="ignore").lower()
                            if re.fullmatch(r"[a-z]{3}", language):
                                stream_languages.append(language)
                    descriptor = value + size
                if stream_type in (3, 4, 15, 17, 0x81, 0x87) or (stream_type == 6 and any(t in tags for t in (0x6a, 0x7a, 0x7b))):
                    languages.extend(stream_languages)
                stream = next_stream
            return list(dict.fromkeys(languages))
        break
    def boxes(blob):
        cursor = 0
        while cursor + 8 <= len(blob):
            size = int.from_bytes(blob[cursor:cursor + 4], "big")
            header = 8
            if size == 1 and cursor + 16 <= len(blob):
                size = int.from_bytes(blob[cursor + 8:cursor + 16], "big")
                header = 16
            if size == 0:
                size = len(blob) - cursor
            if size < header or cursor + size > len(blob):
                break
            yield blob[cursor + 4:cursor + 8], blob[cursor + header:cursor + size]
            cursor += size

    languages = []
    for kind, movie in boxes(data):
        if kind != b"moov":
            continue
        for kind, track in boxes(movie):
            if kind != b"trak":
                continue
            for kind, media in boxes(track):
                if kind != b"mdia":
                    continue
                fields = dict(boxes(media))
                if fields.get(b"hdlr", b"")[8:12] != b"soun":
                    continue
                header = fields.get(b"mdhd", b"")
                position = 32 if header[:1] == b"\x01" else 20
                if len(header) < position + 2:
                    continue
                code = int.from_bytes(header[position:position + 2], "big")
                language = "".join(chr(((code >> shift) & 31) + 96) for shift in (10, 5, 0))
                if re.fullmatch(r"[a-z]{3}", language):
                    languages.append(language)
    return list(dict.fromkeys(languages))


def hls_attributes(line):
    return {key.upper(): (quoted if quoted else plain) for key, quoted, plain in
            re.findall(r'([A-Z0-9-]+)=(?:"([^"]*)"|([^,\s]+))', line, re.I)}


def manifest_uri_lines(text):
    return [line.strip() for line in (text or "").splitlines()
            if line.strip() and not line.lstrip().startswith("#")]


def payload_is_mpeg_ts(data):
    # Validate four consecutive 188-byte packet headers. A Range response can
    # start between packets, so look within the first packet's width.
    if bytes(data[:4]).startswith((b"\x89PNG", b"GIF8", b"\xff\xd8\xff")):
        return False
    for offset in range(min(188, max(0, len(data) - 188 * 3 - 3))):
        if all(data[offset + 188 * packet] == 0x47
               and not (data[offset + 188 * packet + 1] & 0x80)
               and (data[offset + 188 * packet + 3] & 0x30)
               for packet in range(4)):
            return True
    return False


def payload_looks_like_media(data, content_type=""):
    if not data:
        return False
    lowered_type = (content_type or "").lower()
    prefix = bytes(data[:32])
    stripped = prefix.lstrip().lower()
    if prefix.startswith((b"\x89PNG", b"GIF8", b"\xff\xd8\xff")):
        return False
    if stripped.startswith((b"<!doctype", b"<html", b"<script", b"{\"error", b"{\"message")):
        return False
    if (lowered_type.startswith("image/") or "text/html" in lowered_type or
            "json" in lowered_type or "javascript" in lowered_type):
        return payload_is_mpeg_ts(data)
    # MPEG-TS, fragmented MP4, ADTS audio, WebM, encrypted/octet-stream chunks,
    # and other real segments are all larger than the tracking pixels returned by
    # several anti-hotlink decoy manifests.
    return len(data) >= 256


def validate_media_payload(url, headers, result=None, deadline=None):
    if not is_safe_public_url(url):
        return False
    request_headers = dict(headers)
    request_headers["Range"] = "bytes=0-65535" if result is not None else "bytes=0-2047"
    resp = None
    try:
        resp = cffi_req.get(
            url,
            impersonate="chrome120",
            timeout=6 if deadline is None else min(6, max(.1, deadline - time.monotonic())),
            headers=request_headers,
            stream=True)
        if resp.status_code >= 400 or not is_safe_public_url(getattr(resp, "url", url)):
            return False
        content_type = resp.headers.get("content-type", "")
        data = b""
        for chunk in resp.iter_content(chunk_size=2048):
            if deadline is not None and time.monotonic() >= deadline:
                return False
            if chunk:
                data += chunk
            if len(data) >= (65536 if result is not None else 2048):
                break
        data = data[:65536]
        ok = payload_looks_like_media(data, content_type)
        if ok and result is not None:
            result["audio_languages"] = audio_languages_from_payload(data)
        if ok and content_type.lower().startswith("image/"):
            log(f"  Verified MPEG-TS bytes despite Content-Type={content_type}")
        if not ok:
            log(f"  Media payload rejected status={resp.status_code} type={content_type} bytes={len(data)} url={url[:160]}")
        return ok
    except Exception as e:
        log(f"  Media payload validation failed: {e}")
        return False
    finally:
        try:
            if resp is not None:
                resp.close()
        except Exception:
            pass


def validate_hls_result(url, headers, result=None, inspect_alternate_audio=True, deadline=None):
    current_url = url
    english_rendition = None
    validated_variant = None
    for depth in range(3):
        if deadline is not None and time.monotonic() >= deadline:
            return False
        if not is_safe_public_url(current_url):
            return False
        try:
            resp = cffi_req.get(current_url, impersonate="chrome120",
                timeout=6 if deadline is None else min(6, max(.1, deadline - time.monotonic())), headers=headers)
        except Exception as e:
            log(f"  Manifest validation fetch failed: {e}")
            return False
        content_type = resp.headers.get("content-type", "")
        final_url = str(getattr(resp, "url", current_url) or current_url)
        text = resp.text if hasattr(resp, "text") else ""
        if resp.status_code >= 400 or not is_safe_public_url(final_url) or not text.lstrip().startswith("#EXTM3U"):
            log(f"  Manifest validation rejected status={resp.status_code} type={content_type} url={current_url[:160]}")
            return False

        uris = manifest_uri_lines(text)
        if not uris:
            log(f"  Manifest validation rejected an empty playlist: {final_url[:160]}")
            return False

        # A master playlist points at another playlist. A media playlist has
        # EXTINF entries and its non-comment URIs are actual media segments.
        if "#EXT-X-STREAM-INF" in text.upper() and depth < 2:
            lines = text.splitlines()
            variant = next((hls_attributes(line) for line in lines if line.startswith("#EXT-X-STREAM-INF:")), {})
            for line in lines:
                if not line.startswith("#EXT-X-MEDIA:"):
                    continue
                media = hls_attributes(line)
                if (media.get("TYPE") == "AUDIO" and media.get("GROUP-ID") == variant.get("AUDIO") and
                        media.get("LANGUAGE", "").lower() in ("en", "eng", "en-us", "en-gb") and media.get("URI")):
                    english_rendition = urljoin(final_url, media["URI"])
                    break
            current_url = urljoin(final_url, uris[0])
            if validated_variant is None:
                validated_variant = current_url
            continue

        segment_url = urljoin(final_url, uris[0])
        valid_segment = validate_media_payload(segment_url, headers, result) if deadline is None else \
            validate_media_payload(segment_url, headers, result, deadline=deadline)
        if valid_segment:
            if result is not None and validated_variant:
                # Native adaptation must not pick another advertised variant
                # whose segment bytes have never passed this validation.
                result["validated_hls_variant"] = validated_variant
            if result is not None and inspect_alternate_audio and english_rendition:
                valid_audio = validate_hls_result(english_rendition, headers, inspect_alternate_audio=False) if deadline is None else \
                    validate_hls_result(english_rendition, headers, inspect_alternate_audio=False, deadline=deadline)
                if valid_audio:
                    result["audio_languages"] = list(dict.fromkeys(result.get("audio_languages", []) + ["eng"]))
            log(f"  Proxy validation OK manifest={url[:140]} segment={segment_url[:120]}")
            return True
        return False
    return False


def is_proxy_fetchable_result(result):
    if not result or not result.get("url"):
        return False
    if "_proxy_fetchable" in result:
        return result["_proxy_fetchable"]
    url = result["url"]
    if not is_safe_public_url(url):
        log(f"  Proxy validation rejected unsafe URL: {url[:160]}")
        return False
    if not cffi_req:
        log("  Proxy validation skipped: curl_cffi unavailable")
        return True

    headers = build_replay_headers(result)
    set_header_if_missing(headers, "Sec-Fetch-Dest", "empty")
    set_header_if_missing(headers, "Sec-Fetch-Mode", "cors")
    set_header_if_missing(headers, "Sec-Fetch-Site", "cross-site")

    if ".m3u8" in url.lower() or "mpegurl" in url.lower():
        result["_proxy_fetchable"] = validate_hls_result(url, headers, result)
    else:
        result["_proxy_fetchable"] = validate_media_payload(url, headers, result)
    return result["_proxy_fetchable"]


def accept_stream_result(result, stage_name, audio_preference="sub", browser_fallbacks=None):
    if not result:
        return None
    if is_proxy_fetchable_result(result):
        if normalize_audio_preference(audio_preference) == "dub":
            languages = [str(language).lower() for language in result.get("audio_languages", [])
                if language and str(language).lower() not in ("und", "unknown")]
            if not any(language in ("en", "eng") for language in languages):
                # A provider's selected Dub frame is usable when its audio has no
                # language tag. Keep that evidence distinct from embedded English.
                if languages or result.get("selected_audio") != "dub":
                    log(f"  {stage_name}: requested Dub unavailable; audio={languages or 'unknown without a selected Dub frame'}. Continuing extraction.")
                    return None
                log(f"  {stage_name}: selected Dub server has usable media; spoken language remains unverified")
        return result
    if normalize_audio_preference(audio_preference) == "dub":
        log(f"  {stage_name}: unavailable stream does not establish English Dub")
        return None
    fallback_url = prefer_audio_player_url(result.get("referer") or result.get("page_url") or "", audio_preference)
    if fallback_url.startswith("http"):
        fallback = {
            "url": fallback_url,
            "user_agent": result.get("user_agent") or "",
            "cookie": result.get("cookie") or "",
            "referer": fallback_url,
            "requires_webview": True,
        }
        if browser_fallbacks is not None:
            if not browser_fallbacks:
                browser_fallbacks.append(fallback)
            log(f"  {stage_name}: browser-only candidate saved; continuing native extraction")
            return None
        log(f"  {stage_name}: captured stream is browser-only; player page is available for website playback")
        return fallback
    log(f"  {stage_name}: captured stream is browser-only but no player page was available; continuing waterfall")
    return None


def is_probable_media_url(url, content_type=""):
    if not url:
        return False
    lower_url = url.lower()
    lower_type = (content_type or "").lower()
    if lower_url.startswith("blob:"):
        return False
    return (
        ".m3u8" in lower_url
        or lower_url.endswith(".mp4")
        or "mpegurl" in lower_type
        or lower_type.startswith("video/")
    )


def normalize_absolute_url(url, base_url):
    if not url:
        return ""
    url = html_lib.unescape(str(url).strip())
    url = url.replace("\\/", "/")
    if url.startswith("//"):
        return "https:" + url
    if url.startswith(("http://", "https://")):
        return url
    if url.startswith("blob:"):
        return ""
    if base_url:
        return urljoin(base_url, url)
    return ""


def extract_media_urls_from_text(text, base_url=""):
    if not text:
        return []
    candidates = []
    normalized = text.replace("\\/", "/").replace("\\u0026", "&")
    for match in MEDIA_URL_RE.finditer(normalized):
        url = normalize_absolute_url(match.group(0), base_url)
        if url and url not in candidates:
            candidates.append(url)
    return candidates


def extract_dom_media_urls(page):
    urls = []
    try:
        result = page.run_js("""
            return (() => {
                const out = new Set();
                const add = value => {
                    if (!value || typeof value !== 'string') return;
                    if (/\\.m3u8(\\?|$)|\\.mp4(\\?|$)|mpegurl|playlist|manifest/i.test(value)) out.add(value);
                };
                document.querySelectorAll('video,audio,source,track').forEach(el => {
                    add(el.currentSrc);
                    add(el.src);
                    add(el.getAttribute('src'));
                    add(el.getAttribute('data-src'));
                });
                document.querySelectorAll('[data-url],[data-file],[data-src],[data-hls],[data-stream]').forEach(el => {
                    ['data-url','data-file','data-src','data-hls','data-stream'].forEach(name => add(el.getAttribute(name)));
                });
                try {
                    performance.getEntriesByType('resource').forEach(entry => add(entry.name));
                } catch (e) {}
                return Array.from(out);
            })();
        """)
        if isinstance(result, list):
            for item in result:
                url = normalize_absolute_url(item, getattr(page, "url", ""))
                if url and is_probable_media_url(url) and url not in urls:
                    urls.append(url)
    except Exception as e:
        log(f"  DOM media scan failed: {e}")
    return urls


def start_media_listener(page):
    try:
        page.listen.stop()
    except Exception:
        pass
    try:
        page.listen.start(targets=MEDIA_LISTEN_TARGETS)
        return True
    except Exception as e:
        log(f"  Network listener start failed: {e}")
        return False


def stop_media_listener(page):
    try:
        page.listen.stop()
    except Exception:
        pass


def listen_for_media(page, timeout=NETWORK_SNIFF_SECONDS, referer=None):
    deadline = time.time() + max(0.5, timeout)
    while time.time() < deadline:
        slice_timeout = max(0.2, min(1.0, deadline - time.time()))
        try:
            for packet in page.listen.steps(timeout=slice_timeout):
                url = getattr(packet, "url", "") or ""
                if not url:
                    request = getattr(packet, "request", None)
                    url = getattr(request, "url", "") if request else ""
                resp_headers = {}
                try:
                    resp_headers = dict(packet.response.headers) if packet.response else {}
                except Exception:
                    pass
                content_type = resp_headers.get("Content-Type", resp_headers.get("content-type", ""))
                request_headers = extract_packet_request_headers(packet)
                if re.search(r"\.(?:vtt|srt|ass|ssa)(?:\?|$)", url, re.I):
                    track = subtitle_track({"url": url}, page, referer or getattr(page, "url", ""), request_headers)
                    if track:
                        page._ums_subtitle_tracks = (getattr(page, "_ums_subtitle_tracks", []) or []) + [track]
                elif "json" in content_type.lower():
                    try:
                        body = packet.response.body
                        config = json.loads(body) if isinstance(body, (str, bytes)) else body
                        tracks = config_subtitle_tracks(config, page, referer or getattr(page, "url", ""))
                        page._ums_subtitle_tracks = (getattr(page, "_ums_subtitle_tracks", []) or []) + tracks
                    except Exception:
                        pass
                if is_probable_media_url(url, content_type):
                    log(f"  Network: captured media url={url[:180]}")
                    return make_stream_result(url, page, referer or getattr(page, "url", ""), request_headers)
                if content_type and "mpegurl" in content_type.lower():
                    log(f"  Network: captured media by content-type url={url[:180]}")
                    return make_stream_result(url, page, referer or getattr(page, "url", ""), request_headers)
        except Exception as e:
            log(f"  Network listener read failed: {e}")
            return None
    return None


def wait_for_dynamic_player(page, seconds=DYNAMIC_PLAYER_WAIT):
    deadline = time.time() + seconds
    last_state = None
    while time.time() < deadline:
        try:
            iframes = page.eles("tag:iframe", timeout=.2) or []
            server_controls = page.eles("css:[data-link-id]", timeout=.2) or []
            player = page.ele("css:#player", timeout=.2)
            state = (len(iframes), len(server_controls), bool(player))
            if state != last_state:
                log(f"  Dynamic wait: iframes={state[0]} server_controls={state[1]} player={state[2]}")
                last_state = state
            if iframes or server_controls:
                return {"iframes": len(iframes), "server_controls": len(server_controls),
                        "video": bool(page.ele("tag:video", timeout=.1))}
        except Exception:
            pass
        time.sleep(.5)
    log("  Dynamic wait: no player iframe/server controls appeared before timeout")


def click_element(el):
    try:
        el.click()
        return True
    except Exception:
        pass
    try:
        el.click(by_js=True)
        return True
    except Exception:
        return False


def activate_player_controls(page, max_clicks=5, audio_preference="sub", excluded=None, deadline=None):
    selectors = [
        "css:#w-servers [data-link-id]",
        "css:[data-link-id]",
        "css:[data-server]",
        "css:.server",
        "css:.servers li",
        "css:#player",
        "css:video",
        "css:.play-btn",
        "css:.play-button",
        "css:[class*='play']",
        "css:[id*='play']",
    ]
    clicked = 0
    seen = set()
    excluded = excluded if excluded is not None else set()
    for selector in selectors:
        if deadline_expired(deadline):
            return clicked
        try:
            elements = page.eles(selector, timeout=.7) or []
        except Exception:
            elements = []
        ranked = []
        for el in elements[:max_clicks * 2]:
            if deadline_expired(deadline):
                return clicked
            group_audio = ""
            try:
                try:
                    metadata = el.run_js('''
                        return {
                            group: this.closest('[data-type],[data-audio]')?.getAttribute('data-type') ||
                                this.closest('[data-audio]')?.getAttribute('data-audio') || '',
                            values: ['data-link-id','data-server','data-type','aria-label','title','href']
                                .map(name => this.getAttribute(name) || '').concat([(this.innerText || '').slice(0,40)])
                        };
                    ''')
                except Exception:
                    metadata = None
                if isinstance(metadata, dict):
                    group_audio = str(metadata.get("group") or "")
                    values = metadata.get("values") or []
                else:
                    group_audio = metadata if isinstance(metadata, str) else ""
                    values = [el.attr(name) or "" for name in
                              ("data-link-id", "data-server", "data-type", "aria-label", "title", "href")]
                    values.append((el.text or "")[:40])
                signature = "|".join(filter(None, values + [group_audio]))
            except Exception:
                signature = selector
            score = audio_score(signature, audio_preference)
            if group_audio.strip().lower() in ("sub", "subbed", "dub", "dubbed"):
                # Explicit server groups take precedence over text such as
                # "English subtitles", which contains words used by both modes.
                score = 100 if normalize_audio_preference(group_audio) == normalize_audio_preference(audio_preference) else -100
            ranked.append((score, signature, el))

        ranked.sort(key=lambda item: item[0], reverse=True)
        for score, signature, el in ranked:
            if deadline_expired(deadline):
                return clicked
            if score < 0:
                continue
            if signature in seen or signature in excluded:
                continue
            seen.add(signature)
            previous_frames = set(raw_iframe_sources(page)) if score > 0 else set()
            if click_element(el):
                clicked += 1
                if score > 0:
                    excluded.add(signature)
                log(f"  Clicked player/server control selector={selector} audio_score={score}")
                time.sleep(seconds_left(deadline, 1.0, minimum=0))
                if score > 0:
                    page._ums_selected_audio = None
                    selected_frames = [url for url in raw_iframe_sources(page) if url not in previous_frames]
                    # A single changed player frame binds the requested server
                    # to its media. Multiple unrelated frames are not evidence.
                    page._ums_selected_audio_frames = {selected_frames[0]: normalize_audio_preference(audio_preference)} \
                        if len(selected_frames) == 1 else {}
                    return clicked  # Keep the requested audio server selected.
                if clicked >= max_clicks:
                    return clicked
    return clicked


def raw_iframe_sources(page):
    sources = []
    try:
        iframes = page.eles("tag:iframe", timeout=1) or []
        for iframe in iframes:
            try:
                src = normalize_absolute_url(iframe.attr("src") or "", getattr(page, "url", ""))
                if not src or IFRAME_SKIP_RE.search(src):
                    continue
                if src not in sources:
                    sources.append(src)
            except Exception:
                pass
    except Exception as e:
        log(f"  iframe collection failed: {e}")
    return sources


def collect_iframe_sources(page, limit=10, audio_preference="sub"):
    sources = []
    for src in raw_iframe_sources(page):
        for candidate in expand_audio_url_candidates(src, audio_preference):
            if candidate not in sources:
                sources.append(candidate)
    sources.sort(key=lambda src: (
        0 if re.search(r"(stream|embed|player|video|vstream|kwik|rapid|cloud|vid|mega)", src, re.I) else 1,
        -audio_score(src, audio_preference)
    ))
    if sources:
        log(f"  iframe sources={len(sources)} audio={normalize_audio_preference(audio_preference)} first={sources[0][:140]}")
    return sources[:limit]


def set_navigation_referer(page, referer):
    if not referer:
        return
    try:
        headers = {"Referer": referer}
        parsed = urlparse(referer)
        if parsed.scheme and parsed.netloc:
            headers["Origin"] = f"{parsed.scheme}://{parsed.netloc}"
        page.run_cdp("Network.enable")
        page.run_cdp("Network.setExtraHTTPHeaders", headers=headers)
    except Exception as e:
        log(f"  Could not set navigation referer: {e}")

# ---- Site search utilities ---------------------------------------------------

def build_search_urls(site_url, query):
    parsed = urlparse(site_url)
    root = normalize_root(site_url)
    path = (parsed.path or "").rstrip("/")
    q = quote_plus(query)
    candidates = []

    def add(url):
        if url not in candidates:
            candidates.append(url)

    add(f"{root}/search?keyword={q}")
    add(f"{root}/search?query={q}")
    add(f"{root}/search?q={q}")
    if path and path not in ("", "/"):
        add(f"{root}{path}?keyword={q}")
        add(f"{root}{path}?query={q}")
        add(f"{root}{path}/search?keyword={q}")
        add(f"{root}{path}")
    add(f"{root}/search.html?keyword={q}")
    add(f"{root}/search.html?keyw={q}")
    add(f"{root}/anime?keyword={q}")
    add(f"{root}/anime?query={q}")
    add(f"{root}/catalog?keyword={q}")
    add(f"{root}/catalog?query={q}")
    add(f"{root}/catalog?q={q}")
    add(site_url.rstrip("/"))
    return candidates


def is_listing_or_search_url(url):
    try:
        parsed = urlparse(url)
        path = (parsed.path or "/").rstrip("/").lower() or "/"
        query = (parsed.query or "").lower()
    except Exception:
        return False
    if path in ("/", "/home", "/search", "/search.html", "/anime", "/browse", "/catalog"):
        return True
    if "/search" in path:
        return True
    if query and re.search(r"(?:^|&)(keyword|keyw|query|q)=", query):
        if path.count("/") <= 1 or path.endswith(("/anime", "/home", "/search")):
            return True
    return False


def looks_like_candidate_watch_url(url):
    try:
        parsed = urlparse(url)
        path = (parsed.path or "").lower()
    except Exception:
        path = (url or "").lower()
    if is_listing_or_search_url(url):
        return False
    return bool(re.search(r"/(watch|anime|category|series|show|play|episode|info)(?:/|[?#]|$)", path))


def score_candidate_url(full_url, context, query, episode_id, title_aliases=None):
    return max((_score_candidate_title(full_url, context, title, episode_id)
                for title in safe_title_aliases(query, title_aliases)), default=0)


def _score_candidate_title(full_url, context, query, episode_id):
    keywords = query_keywords(query)
    if not keywords:
        return 0
    normalized_query = normalize_title(query)
    required = min(2, len(keywords))
    try:
        parsed = urlparse(full_url)
        path_text = parsed.path.lower()
    except Exception:
        path_text = (full_url or "").lower()
    context = (context or "").lower()
    path_tokens = match_tokens(path_text)
    context_tokens = match_tokens(context)
    matched_words = {word for word in keywords if word in path_tokens or word in context_tokens}
    if len(matched_words) < required:
        return 0
    if path_has_conflicting_title(full_url, keywords):
        return 0
    score = sum(2 for word in keywords if word in path_tokens)
    score += sum(1 for word in keywords if word in context_tokens)
    if all(word in path_tokens for word in keywords):
        score += 5
    if all(word in context_tokens for word in keywords):
        score += 3
    if variant_conflicts(path_text, normalized_query) or variant_conflicts(context, normalized_query):
        return 0
    if str(episode_id).isdigit() and episode_claims(full_url, is_url=True) == {int(episode_id)}:
        score += 8
    return score


def path_has_conflicting_title(full_url, keywords):
    """Reject hydrated/stale cards whose descriptive slug is for another show."""
    try:
        path = urlparse(full_url).path or ""
    except Exception:
        return False
    path_tokens = match_tokens(path)
    if any(word in path_tokens for word in keywords):
        return False

    ignored = {
        "watch", "anime", "category", "series", "show", "play", "episode",
        "episodes", "info", "home", "player", "sub", "dub", "season", "ep"
    }
    for segment in path.strip("/").split("/"):
        raw = segment.strip()
        if not raw or re.fullmatch(r"\d+|ep-?\d+|[a-f0-9]{8,}", raw, re.I):
            continue
        # Human-readable slugs are lowercase words separated by hyphens. Opaque
        # mixed-case player IDs remain eligible when the visible card title matches.
        if raw == raw.lower() and re.fullmatch(r"[a-z][a-z0-9-]*", raw):
            descriptive = [token for token in match_tokens(raw)
                           if token not in ignored and not token.isdigit()]
            if descriptive:
                return True
    return False


def variant_claims(text):
    text = normalize_title(unquote(text or ""))
    roman = {"i": 1, "ii": 2, "iii": 3, "iv": 4, "v": 5, "vi": 6,
             "vii": 7, "viii": 8, "ix": 9, "x": 10}
    claims = {}
    for key, label in (("season", "season"), ("part", "(?:part|cour)")):
        numbers = re.findall(r"\b" + label + r"\s*(\d+|[ivx]+)\b", text)
        numbers += re.findall(r"\b(\d+)(?:st|nd|rd|th)?\s+" + label + r"\b", text)
        if key == "season":
            numbers += re.findall(r"\bs(\d+)(?:e\d+)?\b", text)
        claims[key] = {int(n) if n.isdigit() else roman[n] for n in numbers if n.isdigit() or n in roman}
    claims["kind"] = set(re.findall(r"\b(?:mini anime|specials?|movie|ova|ona)\b", text))
    claims["kind"] = {"special" if n == "specials" else n for n in claims["kind"]}
    return claims


def variant_conflicts(candidate, query):
    actual, expected = variant_claims(candidate), variant_claims(query)
    for key in ("season", "part"):
        if actual[key] and actual[key] - (expected[key] or {1}):
            return True
    return bool(actual["kind"] - expected["kind"])


EPISODE_UNIT_PATTERN = r"[0-9]+(?:[.,_-][0-9]+)*(?:[a-z])?"
EPISODE_PATH_RE = re.compile(
    r"(?P<prefix>(?:/|-)\b(?:episode|ep)[-/]?)(?P<number>" + EPISODE_UNIT_PATTERN +
    r")(?!\w|[.,_-][0-9])", re.I)


def episode_unit_claims(text, is_url=False):
    """Preserve a provider's complete unit token before classifying numbers."""
    if is_url:
        parsed = urlparse(text)
        claims = {m.group("number").lower() for m in EPISODE_PATH_RE.finditer(unquote(parsed.path))}
        claims.update(value.lower() for key, value in parse_qsl(parsed.query)
                      if key.lower() in ("ep", "episode") and re.fullmatch(EPISODE_UNIT_PATTERN, value, re.I))
        return claims
    raw = unquote(str(text or "")).lower()
    numbers = re.findall(r"\b(?:episode|ep)[\s:_-]*(" + EPISODE_UNIT_PATTERN + r")(?!\w|[.,_-][0-9])", raw)
    numbers += re.findall(r"\bs[0-9]+e(" + EPISODE_UNIT_PATTERN + r")(?!\w|[.,_-][0-9])", raw)
    return set(numbers)


def episode_claims(text, is_url=False):
    return {int(number) for number in episode_unit_claims(text, is_url) if number.isdigit()}


class CandidateAnchorParser(HTMLParser):
    """Use each card's own label; adjacent recommendations are not its title."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.anchors = []
        self.current = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "a":
            self.current = [attrs.get("href") or "", [attrs.get("title") or "", attrs.get("aria-label") or ""]]
            self.anchors.append(self.current)
        elif tag == "img" and self.current:
            self.current[1].append(attrs.get("alt") or "")

    def handle_data(self, data):
        if self.current:
            self.current[1].append(data)

    def handle_endtag(self, tag):
        if tag == "a":
            self.current = None


def extract_candidate_links(html, base_url, query, episode_id, title_aliases=None):
    titles = safe_title_aliases(query, title_aliases)
    if not any(query_keywords(title) for title in titles):
        return []
    parser = CandidateAnchorParser()
    parser.feed(html)
    candidates = []
    for href, label in parser.anchors:
        lower = href.lower()
        if href.startswith("#") or href.startswith("javascript:"):
            continue
        if re.search(r"\.(css|js|png|jpg|jpeg|gif|svg|woff2?)(?:[?#]|$)", lower):
            continue
        full = urljoin(base_url, href)
        if not looks_like_candidate_watch_url(full):
            continue
        score = score_candidate_url(full, " ".join(label), query, episode_id, titles)
        if score > 0:
            candidates.append((full, score))

    slug_pattern = re.compile(r"\b[a-z0-9]+(?:-[a-z0-9]+){2,}-[a-z0-9]{4,}\b", re.I)
    for slug in sorted(set(slug_pattern.findall(html))) if not candidates else []:
        slug_text = normalize_title(slug)
        slug_tokens = match_tokens(slug_text)
        score = 0
        for title in titles:
            keywords = query_keywords(title)
            if not keywords or variant_conflicts(slug_text, title):
                continue
            matched = sum(1 for word in keywords if word in slug_tokens)
            if matched >= min(2, len(keywords)):
                score = max(score, matched * 4 + (6 if all(word in slug_tokens for word in keywords) else 0) - 4)
        if score <= 0:
            continue
        ep = episode_id or "1"
        for path in (f"/watch/{slug}?ep={ep}", f"/watch/{slug}/ep-{ep}"):
            candidates.append((urljoin(base_url, path), score))

    candidates.sort(key=lambda item: item[1], reverse=True)
    deduped = []
    for url, score in candidates:
        if url not in deduped:
            deduped.append(url)
    return deduped


def extract_browser_anchor_candidate_links(page, base_url, query, episode_id, title_aliases=None):
    try:
        anchors = page.run_js("""
            return Array.from(document.querySelectorAll('a')).map(a => ({
                href: a.href || a.getAttribute('href') || '',
                text: (a.innerText || a.textContent || '').slice(0, 800),
                label: a.getAttribute('aria-label') || a.getAttribute('title') || '',
                cls: String(a.className || '')
            }));
        """) or []
    except Exception as e:
        log(f"  Browser anchor scan failed: {e}")
        return []

    candidates = []
    for anchor in anchors:
        try:
            href = anchor.get("href") if isinstance(anchor, dict) else ""
            full = urljoin(base_url, href or "")
            if not full or not looks_like_candidate_watch_url(full):
                continue
            context = " ".join(filter(None, [
                anchor.get("text", "") if isinstance(anchor, dict) else "",
                anchor.get("label", "") if isinstance(anchor, dict) else "",
            ]))
            score = score_candidate_url(full, context, query, episode_id, title_aliases)
            # Zero is a rejected title, not a low-ranked match. A watch-path
            # bonus must never revive an unrelated/stale recommendation card.
            if score <= 0:
                continue
            try:
                path = (urlparse(full).path or "").lower()
                if "/watch/" in path:
                    score += 6
                elif "/info/" in path:
                    score -= 2
            except Exception:
                pass
            if score > 0:
                candidates.append((full, score))
        except Exception:
            pass

    candidates.sort(key=lambda item: item[1], reverse=True)
    deduped = []
    for url, _ in candidates:
        if url not in deduped:
            deduped.append(url)
    return deduped


def search_site(site, query, episode_id, deadline=None, title_aliases=None):
    log(f"Search site: {site['name']} [{site['source']}] score={site['score']} url={site['url']}")
    candidates = build_search_urls(site["url"], query)
    timeout_failures = 0
    for index, url in enumerate(candidates):
        if deadline and time.monotonic() >= deadline:
            log(f"  site search deadline reached for {site['name']}; moving on")
            break
        if index >= SITE_SEARCH_URL_LIMIT:
            remaining = len(candidates) - SITE_SEARCH_URL_LIMIT
            if remaining > 0:
                log(f"  search candidate budget used for {site['name']}; skipping {remaining} more pattern(s) and moving on")
            break
        try:
            status, final_url, text = fetch_text(url, timeout=SEARCH_CANDIDATE_TIMEOUT)
            relevant = status == 200 and html_is_relevant(text, query, title_aliases)
            log(f"  candidate search status={status} relevant={relevant} url={url}")
            if not relevant:
                continue
            links = extract_candidate_links(text, final_url, query, episode_id, title_aliases)
            if links:
                log(f"  extracted {len(links)} candidate watch/detail links from {final_url}")
                return links[:5]
            log(f"  relevant page but no watch/detail links: {final_url}")
        except Exception as e:
            log(f"  search candidate failed {url}: {e}")
            if "timed out" in str(e).lower() or "timeout" in str(e).lower():
                timeout_failures += 1
                if timeout_failures >= 2:
                    log(f"  repeated timeouts for {site['name']}; moving on")
                    break
    return []


def browser_search_site(page, site, query, episode_id, deadline=None, title_aliases=None):
    log(f"Browser search site: {site['name']} [{site['source']}] url={site['url']}")
    candidates = build_search_urls(site["url"], query)
    for index, url in enumerate(candidates):
        if deadline_expired(deadline):
            log(f"  browser search deadline reached for {site['name']}; moving on")
            break
        if index >= SITE_SEARCH_URL_LIMIT:
            break
        try:
            if page.get(url, retry=0, timeout=seconds_left(deadline, TIMEOUT_PER_MIRROR)) is False:
                continue  # A failed visit must not reuse the previous search DOM.
            wait_until = time.monotonic() + seconds_left(deadline, 8, minimum=1)
            links = []
            while time.monotonic() < wait_until:
                current_url = getattr(page, "url", "") or url
                html = page.html or ""
                if html_is_relevant(html, query, title_aliases):
                    links = extract_candidate_links(html, current_url, query, episode_id, title_aliases)
                    if not links:
                        links = extract_browser_anchor_candidate_links(page, current_url, query, episode_id, title_aliases)
                    if links:
                        log(f"  browser search extracted {len(links)} candidate links from {current_url}")
                        return links[:5]
                time.sleep(0.75)
            log(f"  browser search no candidate links url={url}")
        except Exception as e:
            log(f"  browser search candidate failed {url}: {e}")
    return []


def episode_url_candidates(base_url, episode_id):
    candidates = []

    def add(url):
        if url and url not in candidates:
            candidates.append(url)

    if str(episode_id).isdigit():
        if any(not unit.isdigit() for unit in episode_unit_claims(base_url, is_url=True)):
            return []  # An explicit special/range needs a mapping, not a guessed rewrite.
        parsed = urlparse(base_url)
        has_path_episode = EPISODE_PATH_RE.search(parsed.path) is not None
        replaced_path = EPISODE_PATH_RE.sub(lambda m: m.group("prefix") + str(episode_id), parsed.path)
        query_pairs = parse_qsl(parsed.query, keep_blank_values=True)
        has_query_episode = any(key.lower() in ("ep", "episode") for key, _ in query_pairs)
        replaced_query = [(key, str(episode_id) if key.lower() in ("ep", "episode") else value)
                          for key, value in query_pairs]
        if has_path_episode or has_query_episode:
            add(parsed._replace(path=replaced_path, query=urlencode(replaced_query)).geturl())
            return candidates  # Never append the original, explicitly wrong unit.
        else:
            episode_path = parsed.path.rstrip("/") + f"/ep-{episode_id}"
            add(parsed._replace(path=episode_path).geturl())
            add(parsed._replace(query=urlencode(query_pairs + [("ep", episode_id)])).geturl())
    add(base_url)
    return candidates

# ---- Extraction stages -------------------------------------------------------

def check_episode_identity(identity, query, episode_id, title_aliases=None, catalog_ids=None, catalog_synonyms=None):
    """Reject observed conflicts. Requested URL parameters are not unit proof."""
    titles = [str(identity.get(key) or "") for key in ("heading", "og", "title")]
    trusted_titles = safe_title_aliases(query, title_aliases)
    synonym = safe_catalog_synonym(query, trusted_titles, catalog_synonyms, catalog_ids)
    accepted_titles = trusted_titles + ([synonym] if synonym else [])
    def matches_title(title, accepted):
        keywords = query_keywords(accepted)
        tokens = match_tokens(title)
        if not keywords or len(set(keywords) & tokens) < min(2, len(keywords)):
            return False
        # Shared subtitle words cannot identify a different primary franchise.
        # Apply this only to a clear multiword prefix before the catalog colon;
        # explicit catalog aliases still supply their own title evidence.
        prefix = query_keywords(accepted.split(":", 1)[0]) if ":" in accepted else []
        if len(prefix) >= 2 and all(len(word) >= 3 for word in prefix):
            return set(prefix).issubset(tokens)
        return True
    trusted_matching = [title for title in titles if any(matches_title(title, accepted) for accepted in trusted_titles)]
    synonym_matching = [title for title in titles if synonym and matches_title(title, synonym)]
    matching = [title for title in titles if title in trusted_matching or title in synonym_matching]
    active = [str(label) for label in identity.get("active_episodes", [])]
    observed_episodes = set().union(*(episode_claims(title) for title in matching + active))
    unmapped_units = set().union(*(episode_unit_claims(label) for label in matching + active))
    for label in active:
        if label.strip().isdigit():
            observed_episodes.add(int(label.strip()))
        elif re.fullmatch(EPISODE_UNIT_PATTERN, label.strip(), re.I):
            unmapped_units.add(label.strip().lower())
    unmapped_units = {unit for unit in unmapped_units if not unit.isdigit()}
    expected_ids = safe_catalog_ids(catalog_ids)
    observed_ids = {"anilist": set(), "mal": set()}
    for claims in [identity.get("primary_ids"), *(identity.get("active_id_claims") or [])]:
        for name, value in safe_catalog_ids(claims).items():
            observed_ids[name].add(value)
    # Only canonical/OG URLs from the current document count; arbitrary links
    # in recommendations or copied request URLs are not provider evidence.
    for link in identity.get("primary_id_urls") or []:
        if not isinstance(link, str):
            continue
        parsed = urlparse(link)
        host = (parsed.hostname or "").lower().rstrip(".")
        kind = "mal" if host in ("myanimelist.net", "www.myanimelist.net") else \
            "anilist" if host in ("anilist.co", "www.anilist.co") else None
        found = re.fullmatch(r"/anime/([1-9]\d{0,9})(?:/.*)?", parsed.path or "")
        if kind and found:
            observed_ids[kind].add(int(found.group(1)))
    id_conflict = any(expected_ids.get(name) and any(value != expected_ids[name] for value in values)
                      for name, values in observed_ids.items())
    id_match = any(expected_ids.get(name) in values for name, values in observed_ids.items()
                   if expected_ids.get(name))
    evidence = {"status": "unknown", "title_match": bool(matching),
                "alias_match": bool(matching) and not any(matches_title(title, query) for title in matching),
                "synonym_match": bool(synonym_matching) and not bool(trusted_matching),
                "catalog_id_match": id_match, "catalog_id_conflict": id_conflict,
                "observed_catalog_ids": {name: sorted(values) for name, values in observed_ids.items() if values},
                "observed_episodes": sorted(observed_episodes), "observed_seasons": [], "observed_parts": [],
                "observed_unmapped_units": sorted(unmapped_units)}
    for title in matching:
        claims = variant_claims(title)
        evidence["observed_seasons"] = sorted(set(evidence["observed_seasons"]) | claims["season"])
        evidence["observed_parts"] = sorted(set(evidence["observed_parts"]) | claims["part"])
    url = identity.get("url") or ""
    conflict = id_conflict or any(variant_conflicts(title, query) for title in matching)
    conflict |= bool(synonym) and (not matching or ((not trusted_matching) and not id_match))
    conflict |= variant_conflicts(unquote(urlparse(url).path), query)
    if str(episode_id).isdigit():
        expected_episode = {int(episode_id)}
        conflict |= bool((observed_episodes | episode_claims(url, is_url=True)) - expected_episode)
        conflict |= bool(unmapped_units or any(
            not unit.isdigit() for unit in episode_unit_claims(url, is_url=True)))
    # A primary heading for a different show is stronger than the search card.
    heading = identity.get("heading") or ""
    heading_matches = any(matches_title(heading, accepted) for accepted in accepted_titles)
    if synonym and matches_title(heading, synonym) and not any(
            matches_title(heading, accepted) for accepted in trusted_titles) and not id_match:
        conflict = True
    heading_without_episode = re.sub(r"\b(?:episode|ep)\s*\d+\b", "", normalize_title(heading)).strip()
    if heading and not heading_matches and heading_without_episode not in (
            "", "watch anime", "watch anime online", "watch online", "player"):
        conflict = True
    if conflict:
        evidence["status"] = "conflict"
    elif matching and observed_episodes:
        expected = variant_claims(query)
        if (not expected["season"] or expected["season"] == set(evidence["observed_seasons"])) and \
           (not expected["part"] or expected["part"] == set(evidence["observed_parts"])):
            evidence["status"] = "consistent"
    return evidence


def read_episode_identity(page, query, episode_id, title_aliases=None, catalog_ids=None, catalog_synonyms=None):
    try:
        identity = page.run_js(r'''
            const active = Array.from(document.querySelectorAll(
                'a.active,button.active,[aria-current="true"],[aria-current="page"],[aria-selected="true"],.episode.active,[data-episode].active'))
                .filter(e => /(?:\/|-)(?:ep|episode)[-/]?\d+\b|[?&](?:ep|episode)=\d+\b/i.test(e.getAttribute('href') || '') ||
                    /\b(?:episode|ep)\s*\d+\b/i.test(e.innerText || '') ||
                    e.hasAttribute('data-episode') || e.closest('[id*="episode"],[class*="episode"]'));
            const heading = document.querySelector('h1');
            const meta = name => document.head.querySelector(`meta[name="${name}"],meta[property="${name}"]`)?.content || '';
            return {
                url: location.href, title: document.title,
                heading: heading?.innerText || '',
                og: document.querySelector('meta[property="og:title"]')?.content || '',
                active_episodes: active.map(e => e.innerText || ''),
                active_id_claims: active.map(e => ({
                    mal: e.getAttribute('data-mal'), anilist: e.getAttribute('data-anilist')
                })),
                primary_ids: {
                    mal: heading?.getAttribute('data-mal') || meta('mal_id') || meta('myanimelist:id'),
                    anilist: heading?.getAttribute('data-anilist') || meta('anilist_id') || meta('anilist:id')
                },
                primary_id_urls: [document.head.querySelector('link[rel="canonical"]')?.href || '',
                    meta('og:url')]
            };
        ''')
        if not isinstance(identity, dict):
            identity = {}
    except Exception:
        identity = {}
    identity.setdefault("url", getattr(page, "url", "") or "")
    evidence = check_episode_identity(identity, query, episode_id, title_aliases, catalog_ids, catalog_synonyms)
    page._ums_match_evidence = evidence
    log(f"  Episode identity: {evidence['status']} seasons={evidence['observed_seasons']} "
        f"parts={evidence['observed_parts']} episodes={evidence['observed_episodes']} "
        f"unmapped_units={evidence['observed_unmapped_units']} "
        f"catalog_alias={evidence['alias_match']} synonym_match={evidence['synonym_match']} "
        f"catalog_id_match={evidence['catalog_id_match']} "
        f"catalog_id_conflict={evidence['catalog_id_conflict']}")
    return evidence["status"] != "conflict"

def stage_a_player_fingerprint(page, html):
    try:
        log("  Stage A: player fingerprint")
        if not any(kw in html for kw in ("megacloud", "vidstreaming", "filemoon", "rapidcloud")):
            log("  Stage A: skipped, no known player markers")
            return None
        script_urls = re.findall(r'<script[^>]+src=["\']([^"\']+)["\'][^>]*>', html, re.I)
        player_scripts = [u for u in script_urls if any(
            kw in u.lower() for kw in ("player", "embed", "video", "source"))]
        for script_url in player_scripts[:3]:
            try:
                if cffi_req:
                    resp = cffi_req.get(script_url, impersonate="chrome120", timeout=6)
                    js = resp.text
                else:
                    import urllib.request
                    with urllib.request.urlopen(script_url, timeout=6) as r:
                        js = r.read().decode("utf-8", errors="ignore")
                charcode_arrays = re.findall(r'String\.fromCharCode\(([0-9,\s]+)\)', js)
                for arr in charcode_arrays:
                    try:
                        chars = [int(x.strip()) for x in arr.split(",")]
                        decoded = "".join(chr(c) for c in chars if 32 <= c < 127)
                        if decoded.startswith("http") and ".m3u8" in decoded:
                            log("  Stage A: found m3u8 in decoded player script")
                            return make_stream_result(decoded, page)
                    except Exception:
                        pass
            except Exception:
                pass
    except Exception as e:
        log(f"  Stage A error: {e}")
    return None


def stage_b_network_sniff(page, timeout=NETWORK_SNIFF_SECONDS, click_controls=True, referer=None, audio_preference="sub", excluded=None, deadline=None):
    try:
        log(f"  Stage B: network sniff audio={normalize_audio_preference(audio_preference)}")
        if deadline_expired(deadline):
            return None
        if not start_media_listener(page):
            return None
        if click_controls:
            clicked = activate_player_controls(page, audio_preference=audio_preference, excluded=excluded, deadline=deadline)
            log(f"  Stage B: controls clicked={clicked}")
            if deadline_expired(deadline):
                return None
            if collect_iframe_sources(page, audio_preference=audio_preference):
                return None  # Inspect the selected frame now rather than idle on its parent.
        result = listen_for_media(page, timeout=seconds_left(deadline, timeout), referer=referer)
        if result:
            return result
    except Exception as e:
        log(f"  Stage B error: {e}")
    finally:
        stop_media_listener(page)
    return None


def extract_player_page(page, src, parent_url, depth=0, visited=None, audio_preference="sub", deadline=None, selected_audio=None, browser_fallbacks=None):
    visited = visited or set()
    src = prefer_audio_player_url(src, audio_preference)
    if depth > 2 or src in visited or deadline_expired(deadline):
        return None
    visited.add(src)
    page._ums_selected_audio = selected_audio

    if is_probable_media_url(src):
        log(f"  Player crawl: iframe src is direct media={src[:160]}")
        return accept_stream_result(make_stream_result(src, page, parent_url), "Direct iframe", audio_preference, browser_fallbacks)

    try:
        log(f"  Player crawl depth={depth}: {src[:180]}")
        page._ums_subtitle_tracks = []
        if start_media_listener(page):
            if page.get(src, retry=0, timeout=seconds_left(deadline, TIMEOUT_PER_MIRROR)) is False:
                return None
            if player_page_unavailable(page):
                log("  Player crawl: selected host reports the file unavailable; trying another source")
                return None
            result = listen_for_media(page, timeout=seconds_left(deadline, PLAYER_MEDIA_WAIT), referer=src)
            stop_media_listener(page)
            accepted = accept_stream_result(result, "Iframe network", audio_preference, browser_fallbacks)
            if accepted:
                return accepted
        else:
            if page.get(src, retry=0, timeout=seconds_left(deadline, TIMEOUT_PER_MIRROR)) is False:
                return None
            time.sleep(2)

        if deadline_expired(deadline):
            return None
        dom_urls = extract_dom_media_urls(page)
        if dom_urls:
            log(f"  Player crawl: DOM media url={dom_urls[0][:160]}")
            for dom_url in dom_urls:
                if deadline_expired(deadline):
                    return None
                accepted = accept_stream_result(make_stream_result(dom_url, page, src), "Iframe DOM", audio_preference, browser_fallbacks)
                if accepted:
                    return accepted

        html = page.html or ""
        result = accept_stream_result(stage_a_player_fingerprint(page, html), "Iframe Stage A", audio_preference, browser_fallbacks)
        if result:
            return result

        result = accept_stream_result(stage_b_network_sniff(page, timeout=seconds_left(deadline, NETWORK_SNIFF_SECONDS), click_controls=True, referer=src, audio_preference=audio_preference, deadline=deadline), "Iframe Stage B", audio_preference, browser_fallbacks)
        if result:
            return result

        if deadline_expired(deadline):
            return None
        result = accept_stream_result(stage_d_carpet_bomb(page, audio_preference), "Iframe Stage D", audio_preference, browser_fallbacks)
        if result:
            return result

        nested = collect_iframe_sources(page, limit=6, audio_preference=audio_preference)
        for nested_src in nested:
            if deadline_expired(deadline):
                return None
            result = extract_player_page(page, nested_src, src, depth + 1, visited, audio_preference=audio_preference,
                deadline=deadline, selected_audio=selected_audio, browser_fallbacks=browser_fallbacks)
            if result:
                return result
    except Exception as e:
        log(f"  Player crawl failed: {e}")
    finally:
        stop_media_listener(page)
    return None


def player_page_unavailable(page):
    try:
        html = page.html or ""
        text = html_lib.unescape(re.sub(r'<[^>]+>', '', html))
        return bool(re.search(r'Error\s+Code\s*:\s*(?:404|410)\b', text, re.I) or
                    re.search(r'<title[^>]*>\s*(?:404|410)\s*(?:[·—\-�]\s*)?(?:Page Not Found|Error|Gone)\b', html, re.I))
    except Exception:
        return False


def stage_c_iframe_crawl(page, parent_url=None, audio_preference="sub", deadline=None, browser_fallbacks=None):
    try:
        log(f"  Stage C: iframe crawl audio={normalize_audio_preference(audio_preference)}")
        sources = collect_iframe_sources(page, limit=10, audio_preference=audio_preference)
        log(f"  Stage C: iframe count={len(sources)}")
        for src in sources:
            if deadline_expired(deadline):
                log("  Stage C: deadline reached before remaining iframe crawl")
                return None
            selected_frames = getattr(page, "_ums_selected_audio_frames", {})
            selected_audio = selected_frames.get(src) if isinstance(selected_frames, dict) else None
            result = extract_player_page(page, src, parent_url or getattr(page, "url", ""), audio_preference=audio_preference,
                deadline=deadline, selected_audio=selected_audio, browser_fallbacks=browser_fallbacks)
            if result:
                return result
    except Exception as e:
        log(f"  Stage C error: {e}")
    return None


def recursive_find_url(obj, depth=0):
    if depth > 6:
        return None
    if isinstance(obj, dict):
        for key in ("file", "url", "src", "source", "stream", "hls", "link"):
            val = obj.get(key, "")
            if isinstance(val, str) and val.startswith("http") and ".m3u8" in val:
                return val
        for v in obj.values():
            r = recursive_find_url(v, depth + 1)
            if r:
                return r
    elif isinstance(obj, list):
        for item in obj:
            r = recursive_find_url(item, depth + 1)
            if r:
                return r
    return None


def stage_d_carpet_bomb(page, audio_preference="sub"):
    try:
        log("  Stage D: inline/carpet scan")
        html = page.html or ""
        ua = get_ua(page)
        cookie = get_cookies_str(page)
        referer = page.url
        def candidate(url):
            result = make_stream_result(url, page, referer)
            return accept_stream_result(result, "Stage D", audio_preference) if normalize_audio_preference(audio_preference) == "dub" else result
        dom_urls = extract_dom_media_urls(page)
        for url in dom_urls:
            result = candidate(url)
            if result:
                log(f"  Stage D: found DOM/performance media url={url[:160]}")
                return result
        urls = extract_media_urls_from_text(html, referer)
        for url in urls:
            result = candidate(url)
            if result:
                log(f"  Stage D: found direct media url={url[:160]}")
                return result
        for script in re.finditer(r'<script[^>]*>(.*?)</script>', html, re.S | re.I):
            text = script.group(1)
            if not any(k in text for k in ("playerConfig", "sources:", "jwplayer",
                                            "setupPlayer", "file:", '"src"')):
                continue
            for json_str in re.finditer(r'\{[^{}]{20,}\}', text):
                try:
                    obj = json.loads(json_str.group())
                    found = recursive_find_url(obj)
                    if found:
                        result = candidate(found)
                        if result:
                            log(f"  Stage D: found stream in inline json={found[:160]}")
                            return result
                except Exception:
                    pass
        for b64 in re.findall(r'[A-Za-z0-9+/]{40,}={0,2}', html):
            try:
                decoded = base64.b64decode(b64 + "==").decode("utf-8", errors="ignore")
                urls = extract_media_urls_from_text(decoded, referer)
                for url in urls:
                    result = candidate(url)
                    if result:
                        log(f"  Stage D: found base64 stream={url[:160]}")
                        return result
            except Exception:
                pass
    except Exception as e:
        log(f"  Stage D error: {e}")
    return None


def extract_from_mirror(mirror, episode_url, page, audio_preference="sub", deadline=None, query=None, episode_id=None, browser_fallbacks=None, title_aliases=None, catalog_ids=None, catalog_synonyms=None):
    match_evidence = None
    try:
        if deadline_expired(deadline):
            return None
        audio_preference = normalize_audio_preference(audio_preference)
        target = episode_url if episode_url.startswith("http") else f"{mirror}/{episode_url.lstrip('/')}"
        page._ums_selected_audio = None
        page._ums_selected_audio_frames = {}
        page._ums_subtitle_tracks = []
        page._ums_match_evidence = None
        log(f"Extract waterfall target: {target} audio={audio_preference}")
        setup_worker_hooks(page)
        set_navigation_referer(page, mirror if mirror else "")
        listening = start_media_listener(page)
        if page.get(target, retry=0, timeout=seconds_left(deadline, TIMEOUT_PER_MIRROR)) is False:
            return None
        if player_page_unavailable(page):
            return None
        ready = wait_for_dynamic_player(page, seconds=seconds_left(deadline, DYNAMIC_PLAYER_WAIT)) or {}
        if query and not read_episode_identity(page, query, episode_id, title_aliases, catalog_ids, catalog_synonyms):
            return None
        observed = getattr(page, "_ums_match_evidence", None)
        if isinstance(observed, dict):
            match_evidence = dict(observed)
        # An empty parent with ready server controls needs selection, not an
        # autoplay sniff. Keep sniffing actual iframes/direct-video parents.
        needs_selection = ready.get("server_controls") and not ready.get("iframes") and not ready.get("video")
        if listening and not needs_selection:
            initial = listen_for_media(page, timeout=seconds_left(deadline, 2), referer=target)
            if audio_preference == "dub" and initial:
                log("  Initial network stream observed, but dub was requested; checking dub controls before accepting autoplay stream")
            result = accept_stream_result(
                initial,
                "Initial network",
                audio_preference, browser_fallbacks)
            stop_media_listener(page)
            if result:
                return result
        else:
            stop_media_listener(page)

        html = page.html or ""
        selected_servers = set()
        if not deadline_expired(deadline):
            result = accept_stream_result(stage_b_network_sniff(page,
                timeout=seconds_left(deadline, NETWORK_SNIFF_SECONDS), referer=target,
                audio_preference=audio_preference, excluded=selected_servers, deadline=deadline), "Audio controls", audio_preference, browser_fallbacks)
            if result:
                return result
        if deadline_expired(deadline):
            return None
        result = accept_stream_result(stage_d_carpet_bomb(page, audio_preference), "Stage D", audio_preference, browser_fallbacks)
        if result:
            return result
        if deadline_expired(deadline):
            return None
        result = accept_stream_result(stage_a_player_fingerprint(page, html), "Stage A", audio_preference, browser_fallbacks)
        if result:
            return result
        if deadline_expired(deadline):
            return None
        result = accept_stream_result(stage_c_iframe_crawl(page, parent_url=target, audio_preference=audio_preference,
            deadline=deadline, browser_fallbacks=browser_fallbacks), "Stage C", audio_preference, browser_fallbacks)
        if result:
            return result
        # A correctly selected server can point at a deleted episode. Keep the
        # other controls for the requested audio within this same lookup budget.
        if selected_servers:
            for _ in range(2):
                if deadline_expired(deadline):
                    return None
                page._ums_subtitle_tracks = []
                page._ums_selected_audio = None
                page._ums_selected_audio_frames = {}
                if page.get(target, retry=0, timeout=seconds_left(deadline, TIMEOUT_PER_MIRROR)) is False:
                    return None
                if player_page_unavailable(page):
                    return None
                wait_for_dynamic_player(page, seconds=seconds_left(deadline, DYNAMIC_PLAYER_WAIT))
                if query and not read_episode_identity(page, query, episode_id, title_aliases, catalog_ids, catalog_synonyms):
                    return None
                before = len(selected_servers)
                result = accept_stream_result(stage_b_network_sniff(page,
                    timeout=seconds_left(deadline, NETWORK_SNIFF_SECONDS), referer=target,
                    audio_preference=audio_preference, excluded=selected_servers, deadline=deadline), "Next audio server", audio_preference, browser_fallbacks)
                if result:
                    return result
                if len(selected_servers) == before:
                    break
                result = accept_stream_result(stage_c_iframe_crawl(page, parent_url=target,
                    audio_preference=audio_preference, deadline=deadline, browser_fallbacks=browser_fallbacks), "Next audio iframe", audio_preference, browser_fallbacks)
                if result:
                    return result
        if not (getattr(page, "url", "") or "").startswith(target):
            log("  Skipping outer-page click sniff after iframe navigation changed the page")
            return None
        if deadline_expired(deadline):
            return None
        result = accept_stream_result(stage_b_network_sniff(page, timeout=seconds_left(deadline, NETWORK_SNIFF_SECONDS), referer=target, audio_preference=audio_preference, deadline=deadline), "Stage B", audio_preference, browser_fallbacks)
        if result:
            return result
    except Exception as e:
        log(f"Extract target failed ({mirror}): {e}")
    finally:
        stop_media_listener(page)
        if match_evidence and browser_fallbacks:
            for fallback in browser_fallbacks:
                fallback.setdefault("match_evidence", match_evidence)
    return None

# ---- Search mode -------------------------------------------------------------

def do_search(query):
    sites = fetch_indexed_sites()
    if not sites:
        return []

    deadline = time.monotonic() + SEARCH_GLOBAL_BUDGET_SECONDS
    selected_sites = sites[:SEARCH_MAX_SITES]
    collected = []
    with ThreadPoolExecutor(max_workers=min(SEARCH_MAX_WORKERS, len(selected_sites))) as executor:
        futures = {
            executor.submit(search_site, site, query, "", deadline): (index, site)
            for index, site in enumerate(selected_sites)
        }
        for future in as_completed(futures):
            index, site = futures[future]
            try:
                links = future.result()
                if links:
                    collected.append((index, site, links))
            except Exception as e:
                log(f"Search on {site.get('name', site.get('url'))} failed: {e}")

    if time.monotonic() >= deadline:
        log("Search global deadline reached; returning partial results")

    results = []
    seen = set()
    for _, site, links in sorted(collected, key=lambda item: item[0]):
        for link in links[:3]:
            key = (site["name"], link)
            if key in seen:
                continue
            seen.add(key)
            results.append({"title": query, "provider": site["name"], "url": link})
            if len(results) >= 36:
                return results
    return results

# ---- Extract mode ------------------------------------------------------------

def do_extract(episode_url, audio_preference="sub", budget_seconds=45):
    audio_preference = normalize_audio_preference(audio_preference)
    try:
        budget_seconds = float(budget_seconds)
    except Exception:
        budget_seconds = 45
    budget_seconds = max(15, min(budget_seconds, 120))
    if not DRISSION_AVAILABLE:
        return {"error": "chromium_not_found"}
    page = launch_browser()
    if page is None:
        return {"error": "chromium_not_found"}
    deadline = time.monotonic() + budget_seconds
    try:
        result = extract_from_mirror("", episode_url, page, audio_preference=audio_preference, deadline=deadline)
        if result:
            return result
        mirrors = fetch_mirror_pool()
        for mirror in mirrors[:5]:
            if deadline_expired(deadline):
                break
            try:
                parsed = urlparse(episode_url)
                mirror_parsed = urlparse(mirror)
                mirror_root = f"{mirror_parsed.scheme}://{mirror_parsed.netloc}" if mirror_parsed.scheme and mirror_parsed.netloc else mirror.rstrip("/")
                reconstructed = f"{mirror_root}{parsed.path}"
                if parsed.query:
                    reconstructed += f"?{parsed.query}"
                result = extract_from_mirror(mirror, reconstructed, page, audio_preference=audio_preference, deadline=deadline)
                if result:
                    return result
            except Exception:
                pass
    finally:
        close_browser(page)
    return {"error": "all_mirrors_failed"}


def do_resolve(query, episode_id, max_site_attempts, audio_preference="sub", budget_seconds=DEFAULT_RESOLVE_BUDGET_SECONDS, prefer_native=True, title_aliases=None, catalog_ids=None, catalog_synonyms=None):
    try:
        max_site_attempts = int(max_site_attempts)
    except (TypeError, ValueError):
        max_site_attempts = 6
    if max_site_attempts < 0:
        max_site_attempts = 6
    audio_preference = normalize_audio_preference(audio_preference)
    try:
        budget_seconds = float(budget_seconds)
    except Exception:
        budget_seconds = DEFAULT_RESOLVE_BUDGET_SECONDS
    budget_seconds = max(35, min(budget_seconds, 240))
    accepted_titles = safe_title_aliases(query, title_aliases)
    catalog_ids = safe_catalog_ids(catalog_ids)
    synonym = safe_catalog_synonym(query, accepted_titles, catalog_synonyms, catalog_ids)
    preferred_title = title_aliases[0] if isinstance(title_aliases, (list, tuple)) and title_aliases and \
        title_aliases[0] in accepted_titles else query
    if not query_keywords(preferred_title):
        if not synonym:
            return {"error": "no_searchable_title"}
        preferred_title = synonym
    sites = fetch_indexed_sites()
    if not sites:
        log("Resolve failed: no indexed sites available")
        return {"error": "no_indexed_sites"}
    if not DRISSION_AVAILABLE:
        log("Resolve failed: DrissionPage/Chromium unavailable")
        return {"error": "chromium_not_found"}

    site_attempt_total = len(sites) if max_site_attempts == 0 else min(max_site_attempts, len(sites))
    site_scope = "all" if max_site_attempts == 0 else str(max_site_attempts)
    log(f"Resolve start query='{query}' search='{preferred_title}' aliases={len(accepted_titles) - 1} "
        f"synonym_fallback={bool(synonym)} "
        f"episode='{episode_id}' max_sites={site_scope} audio={audio_preference} budget={budget_seconds:.0f}s")

    page = launch_browser()
    if page is None:
        return {"error": "chromium_not_found"}

    attempted = 0
    browser_fallback = None
    deadline = time.monotonic() + budget_seconds
    try:
        for site in sites:
            if time.monotonic() >= deadline:
                log("Resolve global deadline reached; returning best available result")
                break
            if max_site_attempts > 0 and attempted >= max_site_attempts:
                break
            attempted += 1
            site_deadline = combine_deadlines(deadline, time.monotonic() + RESOLVE_SITE_BUDGET_SECONDS)
            log(f"Resolve site {attempted}/{site_attempt_total}: {site['name']} ({site['url']})")
            used_synonym = preferred_title == synonym
            links = search_site(site, preferred_title, episode_id, deadline=site_deadline,
                title_aliases=accepted_titles + ([synonym] if used_synonym else []))
            if not links and synonym and not used_synonym and not deadline_expired(site_deadline):
                links = search_site(site, synonym, episode_id, deadline=site_deadline,
                    title_aliases=accepted_titles + [synonym])
                used_synonym = bool(links)
            if not links and not deadline_expired(site_deadline):
                links = browser_search_site(page, site, preferred_title, episode_id,
                    deadline=site_deadline,
                    title_aliases=accepted_titles + ([synonym] if preferred_title == synonym else []))
                used_synonym = preferred_title == synonym and bool(links)
            if not links and synonym and preferred_title != synonym and not deadline_expired(site_deadline):
                links = browser_search_site(page, site, synonym, episode_id,
                    deadline=site_deadline, title_aliases=accepted_titles + [synonym])
                used_synonym = bool(links)
            if not links:
                log(f"Resolve site miss: no candidate links for {site['name']}")
                continue

            tried_urls = set()
            for link in links[:3]:
                if deadline_expired(site_deadline) or deadline_expired(deadline):
                    break
                for candidate in episode_url_candidates(link, episode_id):
                    if deadline_expired(site_deadline):
                        log(f"Resolve site budget reached for {site['name']}; rotating")
                        break
                    if deadline_expired(deadline):
                        log("Resolve global deadline reached during candidate extraction")
                        break
                    if candidate in tried_urls:
                        continue
                    tried_urls.add(candidate)
                    candidate_deadline = combine_deadlines(site_deadline, deadline, time.monotonic() + RESOLVE_CANDIDATE_BUDGET_SECONDS)
                    candidate_fallbacks = [] if prefer_native else None
                    result = extract_from_mirror("", candidate, page, audio_preference=audio_preference,
                        deadline=candidate_deadline, query=query, episode_id=episode_id,
                        browser_fallbacks=candidate_fallbacks, title_aliases=accepted_titles,
                        catalog_ids=catalog_ids, catalog_synonyms=[synonym] if used_synonym else None)
                    if not result and candidate_fallbacks:
                        result = candidate_fallbacks[0]
                    if result:
                        result["site"] = site["name"]
                        result["site_url"] = site["url"]
                        result["attempted_sites"] = attempted
                        evidence = getattr(page, "_ums_match_evidence", None)
                        if isinstance(evidence, dict):
                            result.setdefault("match_evidence", evidence)
                        if result.get("requires_webview"):
                            if not prefer_native:
                                log(f"Resolve explicitly requested website via {site['name']} after {attempted} sites")
                                return result
                            if browser_fallback is None:
                                browser_fallback = result
                            log(f"Resolve browser-only candidate via {site['name']}; continuing current-index native lookup")
                            continue
                        log(f"Resolve success via {site['name']} after {attempted} sites")
                        return result
            log(f"Resolve site exhausted: {site['name']} candidate_links={len(links)} tried_urls={len(tried_urls)}")
    finally:
        close_browser(page)

    if browser_fallback:
        browser_fallback["attempted_sites"] = attempted
        log(f"Resolve found only a website player after {attempted} sites; native playback remains unavailable")
        return browser_fallback
    log(f"Resolve failed after {attempted} indexed sites")
    return {"error": "all_indexed_sites_failed", "attempted_sites": attempted}

# ---- Entry point -------------------------------------------------------------

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(json.dumps({"error": "usage: scraper.py search|extract|resolve <argument>"}))
        sys.exit(1)
    mode = sys.argv[1].lower()
    arg = sys.argv[2]
    try:
        if mode == "search":
            print(json.dumps(do_search(arg), ensure_ascii=False))
        elif mode == "extract":
            audio_pref = sys.argv[3] if len(sys.argv) > 3 else "sub"
            budget_seconds = sys.argv[4] if len(sys.argv) > 4 else 45
            result = do_extract(arg, audio_pref, budget_seconds)
            print(json.dumps({key: value for key, value in result.items() if not key.startswith("_")}, ensure_ascii=False))
        elif mode == "resolve":
            episode_id = sys.argv[3] if len(sys.argv) > 3 else "1"
            max_sites = sys.argv[4] if len(sys.argv) > 4 else "6"
            audio_pref = sys.argv[5] if len(sys.argv) > 5 else "sub"
            budget_seconds = sys.argv[6] if len(sys.argv) > 6 else DEFAULT_RESOLVE_BUDGET_SECONDS
            playback_mode = sys.argv[7] if len(sys.argv) > 7 else "native"
            title_aliases = json.loads(sys.argv[8]) if len(sys.argv) > 8 else []
            catalog_ids = json.loads(sys.argv[9]) if len(sys.argv) > 9 else {}
            catalog_synonyms = json.loads(sys.argv[10]) if len(sys.argv) > 10 else []
            result = do_resolve(arg, episode_id, max_sites, audio_pref, budget_seconds,
                prefer_native=playback_mode != "website", title_aliases=title_aliases,
                catalog_ids=catalog_ids, catalog_synonyms=catalog_synonyms)
            print(json.dumps({key: value for key, value in result.items() if not key.startswith("_")}, ensure_ascii=False))
        else:
            print(json.dumps({"error": f"unknown mode: {mode}"}))
            sys.exit(1)
    except Exception as e:
        print(json.dumps({"error": str(e), "trace": traceback.format_exc()}))
        sys.exit(1)
