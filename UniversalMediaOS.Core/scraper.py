#!/usr/bin/env python3
"""
UniversalMediaOS Bulletproof Scraper v2.0
Stateless CLI: python scraper.py search "Title" | extract "url"
Outputs strict JSON to stdout. All logging to stderr.
"""
import sys, os, re, json, base64, time, traceback, html as html_lib, tempfile, shutil, socket
from typing import Optional
from urllib.parse import quote_plus, urljoin, urlparse

try:
    import curl_cffi.requests as cffi_req
except ImportError:
    cffi_req = None

try:
    from DrissionPage import ChromiumPage, ChromiumOptions
    DRISSION_AVAILABLE = True
except ImportError:
    DRISSION_AVAILABLE = False

# Seed mirror pool (fallback if aggregators unreachable)
SEED_MIRRORS = [
    "https://anitaku.so",
    "https://animepahe.ru",
    "https://hianime.to",
    "https://aniwatchtv.to",
    "https://miruro.tv",
]

TIMEOUT_PER_MIRROR = 12
DYNAMIC_PLAYER_WAIT = 12
NETWORK_SNIFF_SECONDS = 10
PLAYER_MEDIA_WAIT = 20

MEDIA_LISTEN_TARGETS = [
    "m3u8", ".m3u8", "mpegurl", "playlist", "manifest", "master.m3u8", "index.m3u8"
]

MEDIA_URL_RE = re.compile(r'https?://[^\s"\'<>\\]+\.(?:m3u8|mp4)(?:[^\s"\'<>\\]*)?', re.I)
ESCAPED_MEDIA_URL_RE = re.compile(r'https?:\\?/\\?/[^\s"\'<>\\]+\.(?:m3u8|mp4)(?:[^\s"\'<>\\]*)?', re.I)
IFRAME_SKIP_RE = re.compile(
    r"(doubleclick|googlesyndication|google-analytics|google\.com/recaptcha|captcha|"
    r"adservice|adsystem|adtrafficquality|analytics|facebook|twitter|about:blank|javascript:)",
    re.I)

INDEX_SOURCES = [
    "https://everythingmoe.com/section/streaming",
    "https://theindex.moe/items",
]

BLOCKED_SITE_KEYWORDS = (
    "youtube", "crunchyroll", "netflix", "hidive", "bilibili",
    "discord", "github.com", "boards.4chan", "vpn", "manga"
)

def log(message):
    print(f"[scraper] {message}", file=sys.stderr, flush=True)


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


def site_key(url):
    parsed = urlparse(url)
    host = (parsed.netloc or url).lower()
    return host[4:] if host.startswith("www.") else host


def should_skip_site(name, url):
    haystack = f"{name} {url}".lower()
    return any(blocked in haystack for blocked in BLOCKED_SITE_KEYWORDS)


def normalize_title(text):
    return re.sub(r"[^a-z0-9\s]", " ", (text or "").lower()).strip()


def query_keywords(query):
    return [
        word for word in re.findall(r"[a-z0-9]{3,}", normalize_title(query))
        if word not in ("dub", "sub", "eng", "english", "dubbed", "season")
    ]


def html_is_relevant(text, query):
    keywords = query_keywords(query)
    if not keywords:
        return True
    lower = text.lower()
    required = min(2, len(keywords))
    return sum(1 for word in keywords if word in lower) >= required


def add_site(pool, name, url, source, score, rank=999, tags=None):
    if not url or not url.startswith("http"):
        return
    if should_skip_site(name, url):
        return
    pool.append({
        "name": html_lib.unescape(name or site_key(url)).strip(),
        "url": html_lib.unescape(url).strip().rstrip("/"),
        "source": source,
        "score": int(score),
        "rank": int(rank),
        "tags": tags or [],
    })

# ---- Index fetcher ------------------------------------------------------------

def fetch_mirror_pool():
    return [site["url"] for site in fetch_indexed_sites()]


def fetch_indexed_sites():
    pool = []
    log("Fetching streaming site indexes: EverythingMoe + The Index")
    try:
        status, final_url, text = fetch_text("https://everythingmoe.com/section/streaming", timeout=12)
        log(f"EverythingMoe status={status}, bytes={len(text)}")
        if status == 200:
            parse_everythingmoe(text, pool)
    except Exception as e:
        log(f"EverythingMoe fetch failed: {e}")

    try:
        status, final_url, text = fetch_text("https://theindex.moe/items", timeout=12)
        log(f"TheIndex status={status}, bytes={len(text)}")
        if status == 200:
            parse_theindex(text, pool)
    except Exception as e:
        log(f"TheIndex fetch failed: {e}")

    for rank, url in enumerate(SEED_MIRRORS, start=1000):
        add_site(pool, site_key(url), url, "seed", 10, rank=rank, tags=["seed"])

    merged = {}
    for item in pool:
        key = site_key(item["url"])
        current = merged.get(key)
        if current is None or item["score"] > current["score"]:
            merged[key] = item

    ranked = sorted(merged.values(), key=lambda s: (-s["score"], s["rank"], s["name"].lower()))
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

def launch_browser():
    if not DRISSION_AVAILABLE:
        print("[scraper] DrissionPage not available.", file=sys.stderr)
        return None
    profile_dir = tempfile.mkdtemp(prefix="umos_scraper_")
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
        opts.set_argument("--no-sandbox")
        opts.set_argument("--disable-blink-features=AutomationControlled")
        opts.set_argument("--disable-dev-shm-usage")
        opts.set_argument("--mute-audio")
        opts.set_argument("--autoplay-policy=no-user-gesture-required")
        opts.set_argument("--disable-background-networking")
        page = ChromiumPage(addr_or_opts=opts)
        try:
            page._ums_tmp_dir = profile_dir
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
    try:
        page.quit()
    except Exception:
        pass
    if tmp_dir:
        shutil.rmtree(tmp_dir, ignore_errors=True)


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


def make_stream_result(url, page, referer=None):
    return {
        "url": url,
        "user_agent": get_ua(page),
        "cookie": get_cookies_str(page),
        "referer": referer or getattr(page, "url", "") or ""
    }


def is_proxy_fetchable_result(result):
    if not result or not result.get("url"):
        return False
    url = result["url"]
    if ".m3u8" not in url.lower():
        return True
    if not cffi_req:
        log("  Proxy validation skipped: curl_cffi unavailable")
        return True

    headers = {
        "User-Agent": result.get("user_agent") or "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "Accept": "*/*",
        "Accept-Encoding": "identity",
    }
    if result.get("referer"):
        headers["Referer"] = result["referer"]
    if result.get("cookie"):
        headers["Cookie"] = result["cookie"]

    try:
        resp = cffi_req.get(url, impersonate="chrome120", timeout=8, headers=headers)
        content_type = resp.headers.get("content-type", "")
        text = resp.text[:64] if hasattr(resp, "text") else ""
        ok = resp.status_code < 400 and (
            text.lstrip().startswith("#EXTM3U") or "mpegurl" in content_type.lower()
        )
        if ok:
            log(f"  Proxy validation OK status={resp.status_code} url={url[:160]}")
            return True
        log(f"  Proxy validation rejected status={resp.status_code} content_type={content_type} url={url[:160]}")
    except Exception as e:
        log(f"  Proxy validation failed: {e}")
    return False


def accept_stream_result(result, stage_name):
    if not result:
        return None
    if is_proxy_fetchable_result(result):
        return result
    log(f"  {stage_name}: captured stream is browser-only; continuing waterfall")
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
        or "application/vnd.apple.mpegurl" in lower_type
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
    for match in ESCAPED_MEDIA_URL_RE.finditer(text):
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
                if is_probable_media_url(url, content_type):
                    log(f"  Network: captured media url={url[:180]}")
                    return make_stream_result(url, page, referer or getattr(page, "url", ""))
                if content_type and "mpegurl" in content_type.lower():
                    log(f"  Network: captured media by content-type url={url[:180]}")
                    return make_stream_result(url, page, referer or getattr(page, "url", ""))
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
                time.sleep(1.0)
                return
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


def activate_player_controls(page, max_clicks=10):
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
    for selector in selectors:
        try:
            elements = page.eles(selector, timeout=.7) or []
        except Exception:
            elements = []
        for el in elements[:max_clicks]:
            try:
                signature = "|".join(filter(None, [
                    el.attr("data-link-id") or "",
                    el.attr("data-server") or "",
                    el.attr("href") or "",
                    (el.text or "")[:40],
                ]))
            except Exception:
                signature = selector
            if signature in seen:
                continue
            seen.add(signature)
            if click_element(el):
                clicked += 1
                log(f"  Clicked player/server control selector={selector} marker={signature[:80]}")
                time.sleep(1.0)
                if clicked >= max_clicks:
                    return clicked
    return clicked


def collect_iframe_sources(page, limit=10):
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
    sources.sort(key=lambda src: 0 if re.search(r"(stream|embed|player|video|vstream|kwik|rapid|cloud|vid|mega)", src, re.I) else 1)
    if sources:
        log(f"  iframe sources={len(sources)} first={sources[0][:140]}")
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
    add(f"{root}/anime?keyword={q}")
    add(f"{root}/anime?query={q}")
    if path and path not in ("", "/"):
        add(f"{root}{path}?keyword={q}")
        add(f"{root}{path}?query={q}")
        add(f"{root}{path}/search?keyword={q}")
        add(f"{root}{path}")
    add(site_url.rstrip("/"))
    return candidates


def apply_variant_penalties(score, candidate_text, normalized_query):
    season_match = re.search(r"\bseason\s*(\d+)\b|season-(\d+)", candidate_text)
    if season_match:
        season_num = season_match.group(1) or season_match.group(2)
        query_mentions_season = (
            f"season {season_num}" in normalized_query
            or f"s{season_num}" in normalized_query
            or f"{season_num} season" in normalized_query
        )
        if not query_mentions_season:
            score -= 8
    variant_terms = ("mini anime", "special", "specials", "movie", "ova", "ona")
    for term in variant_terms:
        if term in candidate_text and term not in normalized_query:
            score -= 5
    return score


def extract_candidate_links(html, base_url, query, episode_id):
    keywords = query_keywords(query)
    if not keywords:
        return []
    normalized_query = normalize_title(query)
    required = min(2, len(keywords))
    matches = list(re.finditer(r'href\s*=\s*["\']([^"\']+)["\']', html, re.I))
    candidates = []
    for match in matches:
        href = html_lib.unescape(match.group(1))
        lower = href.lower()
        if href.startswith("#") or href.startswith("javascript:"):
            continue
        if re.search(r"\.(css|js|png|jpg|jpeg|gif|svg|woff2?)(?:[?#]|$)", lower):
            continue
        if not re.search(r"/(watch|anime|category|series|show|play|episode|info)(?:/|[?#]|$)", lower):
            continue
        full = urljoin(base_url, href)
        start = max(0, match.start() - 350)
        end = min(len(html), match.end() + 350)
        context = re.sub(r"<[^>]+>", " ", html[start:end]).lower()
        matched_words = {word for word in keywords if word in lower or word in context}
        if len(matched_words) < required:
            continue
        score = sum(2 for word in keywords if word in lower)
        score += sum(1 for word in keywords if word in context)
        if all(word in lower for word in keywords):
            score += 5
        if all(word in context for word in keywords):
            score += 3
        candidate_text = normalize_title(f"{lower} {context}")
        score = apply_variant_penalties(score, candidate_text, normalized_query)
        if episode_id:
            episode_patterns = (
                f"-episode-{episode_id}", f"/episode-{episode_id}", f"/ep-{episode_id}",
                f"ep={episode_id}", f"/{episode_id}"
            )
            if any(p in lower for p in episode_patterns):
                score += 8
        if score > 0:
            candidates.append((full, score))

    slug_pattern = re.compile(r"\b[a-z0-9]+(?:-[a-z0-9]+){2,}-[a-z0-9]{4,}\b", re.I)
    for slug in sorted(set(slug_pattern.findall(html))):
        slug_text = normalize_title(slug)
        matched = sum(1 for word in keywords if word in slug_text)
        if matched < required:
            continue
        score = matched * 4
        if all(word in slug_text for word in keywords):
            score += 6
        score -= 4
        score = apply_variant_penalties(score, slug_text, normalized_query)
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


def search_site(site, query, episode_id):
    log(f"Search site: {site['name']} [{site['source']}] score={site['score']} url={site['url']}")
    for url in build_search_urls(site["url"], query):
        try:
            status, final_url, text = fetch_text(url, timeout=8)
            relevant = status == 200 and html_is_relevant(text, query)
            log(f"  candidate search status={status} relevant={relevant} url={url}")
            if not relevant:
                continue
            links = extract_candidate_links(text, final_url, query, episode_id)
            if links:
                log(f"  extracted {len(links)} candidate watch/detail links from {final_url}")
                return links[:5]
            log(f"  relevant page but no watch/detail links: {final_url}")
        except Exception as e:
            log(f"  search candidate failed {url}: {e}")
    return []


def episode_url_candidates(base_url, episode_id):
    candidates = []

    def add(url):
        if url and url not in candidates:
            candidates.append(url)

    add(base_url)
    if episode_id:
        add(re.sub(r"/ep-\d+(?=/?(?:[?#]|$))", f"/ep-{episode_id}", base_url))
        add(re.sub(r"([?&]ep=)\d+", rf"\g<1>{episode_id}", base_url))
        if "/ep-" not in base_url and "ep=" not in base_url:
            add(base_url.rstrip("/") + f"/ep-{episode_id}")
            sep = "&" if "?" in base_url else "?"
            add(base_url + f"{sep}ep={episode_id}")
    return candidates

# ---- Extraction stages -------------------------------------------------------

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
                            return {"url": decoded, "user_agent": get_ua(page),
                                    "cookie": get_cookies_str(page), "referer": page.url}
                    except Exception:
                        pass
            except Exception:
                pass
    except Exception as e:
        log(f"  Stage A error: {e}")
    return None


def stage_b_network_sniff(page, timeout=NETWORK_SNIFF_SECONDS, click_controls=True, referer=None):
    try:
        log("  Stage B: network sniff")
        if not start_media_listener(page):
            return None
        if click_controls:
            clicked = activate_player_controls(page)
            log(f"  Stage B: controls clicked={clicked}")
        result = listen_for_media(page, timeout=timeout, referer=referer)
        if result:
            return result
    except Exception as e:
        log(f"  Stage B error: {e}")
    finally:
        stop_media_listener(page)
    return None


def extract_player_page(page, src, parent_url, depth=0, visited=None):
    visited = visited or set()
    if depth > 2 or src in visited:
        return None
    visited.add(src)

    if is_probable_media_url(src):
        log(f"  Player crawl: iframe src is direct media={src[:160]}")
        return make_stream_result(src, page, parent_url)

    try:
        log(f"  Player crawl depth={depth}: {src[:180]}")
        if start_media_listener(page):
            page.get(src, timeout=TIMEOUT_PER_MIRROR)
            result = listen_for_media(page, timeout=PLAYER_MEDIA_WAIT, referer=src)
            stop_media_listener(page)
            if result:
                return result
        else:
            page.get(src, timeout=TIMEOUT_PER_MIRROR)
            time.sleep(2)

        dom_urls = extract_dom_media_urls(page)
        if dom_urls:
            log(f"  Player crawl: DOM media url={dom_urls[0][:160]}")
            return make_stream_result(dom_urls[0], page, src)

        html = page.html or ""
        result = stage_a_player_fingerprint(page, html)
        if result:
            return result

        result = stage_b_network_sniff(page, timeout=NETWORK_SNIFF_SECONDS, click_controls=True, referer=src)
        if result:
            return result

        result = stage_d_carpet_bomb(page)
        if result:
            return result

        nested = collect_iframe_sources(page, limit=6)
        for nested_src in nested:
            result = extract_player_page(page, nested_src, src, depth + 1, visited)
            if result:
                return result
    except Exception as e:
        log(f"  Player crawl failed: {e}")
    finally:
        stop_media_listener(page)
    return None


def stage_c_iframe_crawl(page, parent_url=None):
    try:
        log("  Stage C: iframe crawl")
        sources = collect_iframe_sources(page, limit=10)
        log(f"  Stage C: iframe count={len(sources)}")
        for src in sources:
            result = extract_player_page(page, src, parent_url or getattr(page, "url", ""))
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


def stage_d_carpet_bomb(page):
    try:
        log("  Stage D: inline/carpet scan")
        html = page.html or ""
        ua = get_ua(page)
        cookie = get_cookies_str(page)
        referer = page.url
        dom_urls = extract_dom_media_urls(page)
        if dom_urls:
            log(f"  Stage D: found DOM/performance media url={dom_urls[0][:160]}")
            return {"url": dom_urls[0], "user_agent": ua, "cookie": cookie, "referer": referer}
        urls = extract_media_urls_from_text(html, referer)
        if urls:
            log(f"  Stage D: found direct media url={urls[0][:160]}")
            return {"url": urls[0], "user_agent": ua, "cookie": cookie, "referer": referer}
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
                        log(f"  Stage D: found stream in inline json={found[:160]}")
                        return {"url": found, "user_agent": ua, "cookie": cookie, "referer": referer}
                except Exception:
                    pass
        for b64 in re.findall(r'[A-Za-z0-9+/]{40,}={0,2}', html):
            try:
                decoded = base64.b64decode(b64 + "==").decode("utf-8", errors="ignore")
                urls = extract_media_urls_from_text(decoded, referer)
                if urls:
                    log(f"  Stage D: found base64 stream={urls[0][:160]}")
                    return {"url": urls[0], "user_agent": ua, "cookie": cookie, "referer": referer}
            except Exception:
                pass
    except Exception as e:
        log(f"  Stage D error: {e}")
    return None


def extract_from_mirror(mirror, episode_url, page):
    try:
        target = episode_url if episode_url.startswith("http") else f"{mirror}/{episode_url.lstrip('/')}"
        log(f"Extract waterfall target: {target}")
        setup_worker_hooks(page)
        set_navigation_referer(page, mirror if mirror else "")
        listening = start_media_listener(page)
        page.get(target, timeout=TIMEOUT_PER_MIRROR)
        wait_for_dynamic_player(page)
        if listening:
            result = accept_stream_result(
                listen_for_media(page, timeout=2, referer=target),
                "Initial network")
            stop_media_listener(page)
            if result:
                return result
        else:
            stop_media_listener(page)

        html = page.html or ""
        result = accept_stream_result(stage_d_carpet_bomb(page), "Stage D")
        if result:
            return result
        result = accept_stream_result(stage_a_player_fingerprint(page, html), "Stage A")
        if result:
            return result
        result = accept_stream_result(stage_c_iframe_crawl(page, parent_url=target), "Stage C")
        if result:
            return result
        if not (getattr(page, "url", "") or "").startswith(target):
            log("  Skipping outer-page click sniff after iframe navigation changed the page")
            return None
        result = accept_stream_result(stage_b_network_sniff(page, referer=target), "Stage B")
        if result:
            return result
    except Exception as e:
        log(f"Extract target failed ({mirror}): {e}")
    finally:
        stop_media_listener(page)
    return None

# ---- Search mode -------------------------------------------------------------

def do_search(query):
    results = []
    seen = set()
    sites = fetch_indexed_sites()
    for site in sites[:8]:
        try:
            links = search_site(site, query, "")
            for link in links[:5]:
                key = (site["name"], link)
                if key in seen:
                    continue
                seen.add(key)
                results.append({"title": query, "provider": site["name"], "url": link})
            if len(results) >= 20:
                break
        except Exception as e:
            log(f"Search on {site.get('name', site.get('url'))} failed: {e}")
    return results

# ---- Extract mode ------------------------------------------------------------

def do_extract(episode_url):
    if not DRISSION_AVAILABLE:
        return {"error": "chromium_not_found"}
    page = launch_browser()
    if page is None:
        return {"error": "chromium_not_found"}
    try:
        result = extract_from_mirror("", episode_url, page)
        if result:
            return result
        mirrors = fetch_mirror_pool()
        for mirror in mirrors[:5]:
            try:
                from urllib.parse import urlparse
                parsed = urlparse(episode_url)
                reconstructed = f"{mirror}{parsed.path}"
                if parsed.query:
                    reconstructed += f"?{parsed.query}"
                result = extract_from_mirror(mirror, reconstructed, page)
                if result:
                    return result
            except Exception:
                pass
    finally:
        close_browser(page)
    return {"error": "all_mirrors_failed"}


def do_resolve(query, episode_id, max_site_attempts):
    max_site_attempts = max(1, min(int(max_site_attempts), 30))
    log(f"Resolve start query='{query}' episode='{episode_id}' max_sites={max_site_attempts}")
    sites = fetch_indexed_sites()
    if not sites:
        log("Resolve failed: no indexed sites available")
        return {"error": "no_indexed_sites"}
    if not DRISSION_AVAILABLE:
        log("Resolve failed: DrissionPage/Chromium unavailable")
        return {"error": "chromium_not_found"}

    page = launch_browser()
    if page is None:
        return {"error": "chromium_not_found"}

    attempted = 0
    try:
        for site in sites:
            if attempted >= max_site_attempts:
                break
            attempted += 1
            log(f"Resolve site {attempted}/{max_site_attempts}: {site['name']} ({site['url']})")
            links = search_site(site, query, episode_id)
            if not links:
                log(f"Resolve site miss: no candidate links for {site['name']}")
                continue

            tried_urls = set()
            for link in links[:3]:
                for candidate in episode_url_candidates(link, episode_id):
                    if candidate in tried_urls:
                        continue
                    tried_urls.add(candidate)
                    result = extract_from_mirror("", candidate, page)
                    if result:
                        result["site"] = site["name"]
                        result["site_url"] = site["url"]
                        result["attempted_sites"] = attempted
                        log(f"Resolve success via {site['name']} after {attempted} sites")
                        return result
            log(f"Resolve site exhausted: {site['name']} candidate_links={len(links)} tried_urls={len(tried_urls)}")
    finally:
        close_browser(page)

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
            print(json.dumps(do_extract(arg), ensure_ascii=False))
        elif mode == "resolve":
            episode_id = sys.argv[3] if len(sys.argv) > 3 else "1"
            max_sites = sys.argv[4] if len(sys.argv) > 4 else "6"
            print(json.dumps(do_resolve(arg, episode_id, max_sites), ensure_ascii=False))
        else:
            print(json.dumps({"error": f"unknown mode: {mode}"}))
            sys.exit(1)
    except Exception as e:
        print(json.dumps({"error": str(e), "trace": traceback.format_exc()}))
        sys.exit(1)
