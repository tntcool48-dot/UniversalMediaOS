#!/usr/bin/env python3
"""
UniversalMediaOS Bulletproof Scraper v2.0
Stateless CLI: python scraper.py search "Title" | extract "url"
Outputs strict JSON to stdout. All logging to stderr.
"""
import sys, os, re, json, base64, time, traceback, html as html_lib
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

TIMEOUT_PER_MIRROR = 8

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
    try:
        opts = ChromiumOptions()
        opts.set_argument("--no-sandbox")
        opts.set_argument("--disable-blink-features=AutomationControlled")
        opts.set_argument("--disable-dev-shm-usage")
        opts.set_argument("--mute-audio")
        opts.set_argument("--autoplay-policy=no-user-gesture-required")
        page = ChromiumPage(addr_or_opts=opts)
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
        print(f"[scraper] Browser launch failed: {e}", file=sys.stderr)
        return None


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
    return candidates


def extract_candidate_links(html, base_url, query, episode_id):
    keywords = query_keywords(query)
    if not keywords:
        return []
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
        score = sum(2 for word in keywords if word in lower)
        score += sum(1 for word in keywords if word in context)
        if episode_id:
            episode_patterns = (
                f"-episode-{episode_id}", f"/episode-{episode_id}", f"/ep-{episode_id}",
                f"ep={episode_id}", f"/{episode_id}"
            )
            if any(p in lower for p in episode_patterns):
                score += 8
        if score > 0:
            candidates.append((full, score))
    candidates.sort(key=lambda item: item[1], reverse=True)
    return [url for url, score in candidates]


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


def stage_b_network_sniff(page):
    try:
        log("  Stage B: network sniff")
        page.listen.start(targets=[".m3u8", "m3u8", "playlist", "manifest"])
        for selector in ["video", ".play-btn", ".play-button", "#player", ".jw-video",
                         "[class*='play']", "[id*='play']"]:
            try:
                el = page.ele(f"css:{selector}", timeout=1)
                if el:
                    el.click()
                    break
            except Exception:
                pass
        deadline = time.time() + 5
        for packet in page.listen.steps(timeout=5):
            if time.time() > deadline:
                break
            url = getattr(packet, "url", "") or ""
            resp_headers = {}
            try:
                resp_headers = dict(packet.response.headers) if packet.response else {}
            except Exception:
                pass
            ct = resp_headers.get("Content-Type", resp_headers.get("content-type", ""))
            if ".m3u8" in url or "mpegURL" in ct or "mpegurl" in ct.lower():
                log(f"  Stage B: captured stream url={url[:160]}")
                return {"url": url, "user_agent": get_ua(page),
                        "cookie": get_cookies_str(page), "referer": page.url}
        page.listen.stop()
    except Exception as e:
        log(f"  Stage B error: {e}")
    return None


def stage_c_iframe_crawl(page):
    try:
        log("  Stage C: iframe crawl")
        iframes = page.eles("tag:iframe") or []
        log(f"  Stage C: iframe count={len(iframes)}")
        for iframe in iframes[:5]:
            try:
                src = iframe.attr("src") or ""
                if not re.search(r"(embed|player|video|m3u8|vstream|kwik|rapid|cloud)", src, re.I):
                    continue
                if src.startswith("//"):
                    src = "https:" + src
                elif not src.startswith("http"):
                    continue
                page.get(src)
                time.sleep(1)
                html = page.html or ""
                result = stage_a_player_fingerprint(page, html)
                if result:
                    return result
                result = stage_b_network_sniff(page)
                if result:
                    return result
            except Exception:
                pass
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
        urls = re.findall(r'https?://[^\s"\'<>\\]+\.(?:m3u8|mp4)[^\s"\'<>\\]*', html)
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
                if decoded.startswith("http") and ".m3u8" in decoded:
                    log(f"  Stage D: found base64 stream={decoded[:160]}")
                    return {"url": decoded, "user_agent": ua, "cookie": cookie, "referer": referer}
            except Exception:
                pass
    except Exception as e:
        log(f"  Stage D error: {e}")
    return None


def extract_from_mirror(mirror, episode_url, page):
    try:
        target = episode_url if episode_url.startswith("http") else f"{mirror}/{episode_url.lstrip('/')}"
        log(f"Extract waterfall target: {target}")
        page.get(target, timeout=TIMEOUT_PER_MIRROR)
        time.sleep(2)
        setup_worker_hooks(page)
        html = page.html or ""
        result = stage_a_player_fingerprint(page, html)
        if result:
            return result
        result = stage_b_network_sniff(page)
        if result:
            return result
        result = stage_c_iframe_crawl(page)
        if result:
            return result
        result = stage_d_carpet_bomb(page)
        if result:
            return result
    except Exception as e:
        log(f"Extract target failed ({mirror}): {e}")
    return None

# ---- Search mode -------------------------------------------------------------

def do_search(query):
    results = []
    sites = fetch_indexed_sites()
    for site in sites[:10]:
        try:
            links = search_site(site, query, "")
            for link in links[:5]:
                results.append({"title": query, "provider": site["name"], "url": link})
            if results:
                break
        except Exception as e:
            log(f"Search on {site.get('name', site.get('url'))} failed: {e}")
    return results

# ---- Extract mode ------------------------------------------------------------

def do_extract(episode_url):
    if not DRISSION_AVAILABLE:
        return {"error": "chromium_not_found"}
    mirrors = fetch_mirror_pool()
    page = launch_browser()
    if page is None:
        return {"error": "chromium_not_found"}
    try:
        result = extract_from_mirror("", episode_url, page)
        if result:
            return result
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
        try:
            page.quit()
        except Exception:
            pass
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
        try:
            page.quit()
        except Exception:
            pass

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
