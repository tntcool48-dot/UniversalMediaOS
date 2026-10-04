#!/usr/bin/env python3
import html as html_lib
import hashlib
import json
import re
import sys
import time
import urllib.parse
import urllib.request
import http.cookiejar
from collections import deque
from typing import NamedTuple

import curl_cffi.requests as cffi_req
import httpx
from bs4 import BeautifulSoup


USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
)
MAX_PAGE_BYTES = 2 * 1024 * 1024
FORMATS = ("epub", "pdf")
FILE_URL_RE = re.compile(
    r"\.(?:epub|pdf)(?:$|[?&#])", re.I)
BOOK_HREF_RE = re.compile(r"/(md5|book)/([^/?#]+)", re.I)
SIZE_RE = re.compile(r"\b\d+(?:\.\d+)?\s*(?:TB|GB|MB|KB|B)\b", re.I)
YEAR_RE = re.compile(r"\b(?:18|19|20)\d{2}\b")
DOWNLOAD_WORDS = (
    "download", "mirror", "slow", "fast", "libgen",
    "library.lol", "ipfs", "z-lib", "zlibrary", "gateway",
)
CURL_SESSION = cffi_req.Session()
HTTPX_CLIENT = httpx.Client(follow_redirects=True)
URLLIB_OPENER = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


class Response(NamedTuple):
    status: int
    url: str
    headers: dict
    body: bytes


def _headers(extra=None):
    values = {
        "User-Agent": USER_AGENT,
        "Accept": "text/html,application/xhtml+xml,application/pdf,application/epub+zip,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.8",
        "Range": f"bytes=0-{MAX_PAGE_BYTES - 1}",
    }
    values.update(extra or {})
    return values


def _read_chunks(chunks):
    body = bytearray()
    for chunk in chunks:
        if not chunk:
            continue
        remaining = MAX_PAGE_BYTES - len(body)
        body.extend(chunk[:remaining])
        if len(body) >= MAX_PAGE_BYTES:
            break
    return bytes(body)


def _curl_fetch(url, timeout, referer=""):
    response = CURL_SESSION.get(
        url,
        headers=_headers({"Referer": referer} if referer else None),
        impersonate="chrome124",
        timeout=timeout,
        allow_redirects=True,
        stream=True,
    )
    body = _read_chunks(response.iter_content(chunk_size=65536))
    result = Response(
        response.status_code,
        str(response.url),
        {str(k).lower(): str(v) for k, v in response.headers.items()},
        body,
    )
    response.close()
    return result


def _httpx_fetch(url, timeout, referer=""):
    with HTTPX_CLIENT.stream(
        "GET",
        url,
        headers=_headers({"Referer": referer} if referer else None),
        timeout=timeout,
    ) as response:
        return Response(
            response.status_code,
            str(response.url),
            {str(k).lower(): str(v) for k, v in response.headers.items()},
            _read_chunks(response.iter_bytes()),
        )


def _urllib_fetch(url, timeout, referer=""):
    request = urllib.request.Request(
        url,
        headers=_headers({"Referer": referer} if referer else None))
    with URLLIB_OPENER.open(request, timeout=timeout) as response:
        return Response(
            response.status,
            response.geturl(),
            {str(k).lower(): str(v) for k, v in response.headers.items()},
            response.read(MAX_PAGE_BYTES),
        )


def fetch(url, deadline, referer=""):
    last_error = None
    last_response = None
    clients = (_curl_fetch, _httpx_fetch, _urllib_fetch)
    request_deadline = min(deadline, time.monotonic() + 5)
    for index, client in enumerate(clients):
        remaining = request_deadline - time.monotonic()
        if remaining <= 0:
            break
        try:
            timeout = max(.5, remaining / (len(clients) - index))
            response = client(url, timeout, referer)
            if response.status not in (403, 408, 429) and response.status < 500:
                return response
            last_response = response
        except Exception as error:
            last_error = error
    if last_response:
        return last_response
    if last_error:
        raise last_error
    raise TimeoutError(url)


def decode_html(response):
    content_type = response.headers.get("content-type", "")
    charset_match = re.search(r"charset=([^;\s]+)", content_type, re.I)
    charset = charset_match.group(1).strip('"\'') if charset_match else "utf-8"
    return response.body.decode(charset, errors="ignore")


def clean_text(value):
    return " ".join(html_lib.unescape(str(value or "")).split())


def identifier_from_href(href):
    match = BOOK_HREF_RE.search(urllib.parse.unquote(str(href or "")))
    if not match:
        return ""
    value = match.group(2).strip()
    return value.lower() if re.fullmatch(r"[a-f0-9]{32}", value, re.I) else value


def format_from_text(text):
    lowered = clean_text(text).lower()
    for file_format in FORMATS:
        if file_format in lowered:
            return file_format
    return "unknown"


def metadata_value(card, itemprop, property_class, label):
    element = card.select_one(f'[itemprop="{itemprop}"]')
    if element:
        return clean_text(element.get_text(" ", strip=True))
    element = card.select_one(f".{property_class} .property_value")
    if element:
        return clean_text(element.get_text(" ", strip=True))
    text = clean_text(card.get_text(" ", strip=True))
    match = re.search(rf"\b{re.escape(label)}\s*:\s*(.+?)(?=\s+(?:Year|Language|File|Format|Size|ISBN(?:-1[03])?|Publisher)\s*:|$)", text, re.I)
    return clean_text(match.group(1)) if match else ""


def find_book_card(anchor, identifier):
    best = anchor
    best_length = len(clean_text(anchor.get_text(" ", strip=True)))
    for parent in anchor.parents:
        if getattr(parent, "name", "") in ("body", "html", "[document]"):
            break
        text_length = len(clean_text(parent.get_text(" ", strip=True)))
        if text_length > 3000:
            break
        classes = " ".join(parent.get("class", [])) if hasattr(parent, "get") else ""
        ids = {
            identifier_from_href(link.get("href"))
            for link in parent.select("a[href]")
            if identifier_from_href(link.get("href"))
        }
        if ids == {identifier} and text_length >= best_length:
            best = parent
            best_length = text_length
        if re.search(r"book.?row|aarecord|search.?result|result.?item|book.?item", classes, re.I):
            return parent
    return best


def parse_search_html(page_html):
    soup = BeautifulSoup(page_html, "lxml")
    grouped = {}
    for anchor in soup.select("a[href]"):
        identifier = identifier_from_href(anchor.get("href"))
        if identifier:
            grouped.setdefault(identifier, []).append(anchor)

    results = []
    for identifier, anchors in grouped.items():
        anchor = max(anchors, key=lambda item: len(clean_text(item.get_text(" ", strip=True))))
        card = find_book_card(anchor, identifier)

        title_element = card.select_one('[itemprop="name"], h3, h2, .title, .book-title')
        title = clean_text(title_element.get_text(" ", strip=True)) if title_element else ""
        if not title:
            title = clean_text(anchor.get_text(" ", strip=True))
        if not title:
            image = card.select_one("img[alt]")
            title = clean_text(image.get("alt")) if image else "Unknown Title"

        authors = []
        for element in card.select('[itemprop="author"], .authors a, .author a, .author'):
            author = clean_text(element.get_text(" ", strip=True))
            if author and author not in authors:
                authors.append(author)
        if not authors:
            author_text = metadata_value(card, "author", "property_author", "Author")
            authors = [part.strip() for part in re.split(r"\s*[;,]\s*", author_text) if part.strip()]

        card_text = clean_text(card.get_text(" ", strip=True))
        file_format = format_from_text(
            metadata_value(card, "encodingFormat", "property__file", "File") or card_text)
        if file_format == "unknown":
            continue
        language = metadata_value(card, "inLanguage", "property_language", "Language")
        year_text = metadata_value(card, "datePublished", "property_year", "Year")
        year_match = YEAR_RE.search(year_text or card_text)
        size_match = SIZE_RE.search(card_text)

        isbn_fields = [element.get('content') or element.get_text(' ', strip=True)
                       for element in card.select('[itemprop="isbn"], .property_isbn .property_value, '
                                                  '.property_isbn13 .property_value, .property_isbn10 .property_value')]
        isbn_fields.extend(metadata_value(card, 'isbn', 'property_isbn', label)
                           for label in ('ISBN', 'ISBN-10', 'ISBN-13'))
        isbns = list(dict.fromkeys(clean_text(match.group(0)) for field in isbn_fields
                    for match in re.finditer(r'(?<!\d)(?:97[89][ -]*)?(?:\d[ -]*){9}[\dXx](?!\d)', field)))
        results.append({
            "id": identifier,
            "title": title or "Unknown Title",
            "authors": authors or ["Unknown Author"],
            "format": file_format,
            "size": size_match.group(0) if size_match else "",
            "language": language,
            "year": int(year_match.group(0)) if year_match else None,
            "md5": identifier if re.fullmatch(r'[a-f0-9]{32}', identifier, re.I) else '',
            "isbns": isbns,
            "publisher": metadata_value(card, 'publisher', 'property_publisher', 'Publisher'),
        })
    return results


def mirror_roots(mirror_url):
    parsed = urllib.parse.urlparse(mirror_url)
    roots = [mirror_url.rstrip("/")]
    host = (parsed.hostname or "").lower()
    if host.startswith("annas-archive."):
        for suffix in ("cc", "li", "se", "org"):
            candidate_host = f"annas-archive.{suffix}"
            candidate = urllib.parse.urlunparse((
                parsed.scheme or "https",
                candidate_host,
                "",
                "",
                "",
                "",
            ))
            if candidate not in roots:
                roots.append(candidate)
    return roots


def search_urls(query, mirror_url):
    root = mirror_url.rstrip("/")
    encoded = urllib.parse.quote_plus(query)
    return [
        f"{root}/search?q={encoded}",
        f"{root}/s/?q={encoded}",
        f"{root}/search?index=&q={encoded}",
    ]


def do_search(query, mirror_url="https://annas-archive.org"):
    deadline = time.monotonic() + 20
    saw_empty_results = False
    saw_timeout = False
    roots = mirror_roots(mirror_url)
    routed_urls = [search_urls(query, root) for root in roots]
    for route_index in range(max(len(urls) for urls in routed_urls)):
        for urls in routed_urls:
            if route_index >= len(urls):
                continue
            url = urls[route_index]
            if time.monotonic() >= deadline:
                if saw_empty_results:
                    return []
                raise TimeoutError("book search deadline exceeded")
            try:
                response = fetch(url, deadline)
            except TimeoutError:
                saw_timeout = True
                continue
            except Exception:
                continue
            if response.status >= 400:
                continue
            page = decode_html(response)
            results = parse_search_html(page)
            if results:
                return results
            text = BeautifulSoup(page, "html.parser").get_text(" ", strip=True)
            if re.search(r"\b(?:no (?:results|files|matches) found|nothing found|0 results)\b", text, re.I):
                saw_empty_results = True
    if saw_empty_results:
        return []
    if saw_timeout or time.monotonic() >= deadline:
        raise TimeoutError("book search deadline exceeded")
    raise RuntimeError("book search returned no usable results page")


def is_file_url(url):
    decoded = urllib.parse.unquote(url)
    return bool(FILE_URL_RE.search(decoded))


def is_media_response(response):
    content_type = response.headers.get("content-type", "").lower()
    disposition = response.headers.get("content-disposition", "").lower()
    prefix = response.body[:16]
    declared_supported_file = bool(
        FILE_URL_RE.search(response.url)
        or re.search(r"\.(?:epub|pdf)(?=[\"';\s]|$)", disposition, re.I))
    if "text/html" in content_type or prefix.lstrip().lower().startswith((b"<!doctype", b"<html")):
        return False
    return (
        content_type.startswith(("application/pdf", "application/epub+zip"))
        or ("application/octet-stream" in content_type and declared_supported_file)
        or prefix.startswith(b"%PDF-")
        or (prefix.startswith(b"PK\x03\x04") and declared_supported_file)
    )


def link_name(url, text=""):
    value = f"{text} {url}".lower()
    host = urllib.parse.urlparse(url).hostname or "Mirror"
    tail = urllib.parse.unquote(urllib.parse.urlparse(url).path).rstrip("/").split("/")[-1][:16]
    identity = hashlib.sha1(url.encode("utf-8")).hexdigest()[:8]
    suffix = f" — {host}/{tail} [{identity}]" if tail else f" — {host} [{identity}]"
    if "library.lol" in value or "libgen" in value:
        return "Library Genesis" + suffix
    if "ipfs" in value:
        return "IPFS Gateway" + suffix
    if "slow" in value:
        return "Slow Download" + suffix
    if "fast" in value:
        return "Fast Download" + suffix
    file_format = format_from_text(value)
    return f"{file_format.upper()}{suffix}" if file_format != "unknown" else f"{host} [{identity}]"


def page_links(page_html, base_url):
    soup = BeautifulSoup(page_html, "lxml")
    links = []
    for anchor in soup.select("a[href], form[action]"):
        href = anchor.get("href") or anchor.get("action") or ""
        absolute = urllib.parse.urljoin(base_url, html_lib.unescape(href).replace("\\/", "/"))
        marker = clean_text(anchor.get_text(" ", strip=True))
        haystack = f"{absolute} {marker}".lower()
        if absolute.startswith(("http://", "https://")) and (
                is_file_url(absolute) or any(word in haystack for word in DOWNLOAD_WORDS)):
            links.append((absolute, link_name(absolute, marker)))

    normalized = html_lib.unescape(page_html).replace("\\/", "/").replace("\\u0026", "&")
    for match in re.finditer(r'https?://[^\s"\'<>]+', normalized, re.I):
        url = match.group(0).rstrip("),.;]")
        lowered = url.lower()
        if is_file_url(url) or any(word in lowered for word in DOWNLOAD_WORDS):
            links.append((url, link_name(url)))

    unique = []
    seen = set()
    for url, name in links:
        if url not in seen:
            seen.add(url)
            unique.append((url, name))
    return unique


def detail_urls(identifier, mirror_url):
    root = mirror_url.rstrip("/")
    if re.fullmatch(r"[a-f0-9]{32}", identifier, re.I):
        return [f"{root}/md5/{identifier}", f"{root}/book/{identifier}"]
    return [f"{root}/book/{urllib.parse.quote(identifier)}"]


def crawl_downloads(start_urls, deadline):
    direct = []
    queue = deque()
    for item in start_urls:
        url, name, depth = item[:3]
        referer = item[3] if len(item) > 3 else ""
        if is_file_url(url):
            direct.append({"name": name, "url": url})
        else:
            queue.append((url, name, depth, referer))
    visited = set()

    while queue and time.monotonic() < deadline:
        url, name, depth, referer = queue.popleft()
        if url in visited:
            continue
        visited.add(url)
        if is_file_url(url):
            direct.append({"name": name, "url": url})
            continue
        try:
            response = fetch(url, deadline, referer)
        except Exception:
            continue
        if response.status >= 400:
            continue
        if is_media_response(response):
            direct.append({"name": link_name(response.url, name), "url": response.url})
            continue
        if depth >= 2:
            continue
        for child_url, child_name in page_links(decode_html(response), response.url):
            queue.append((child_url, child_name, depth + 1, response.url))

    unique = []
    seen = set()
    for result in direct:
        if result["url"] not in seen:
            seen.add(result["url"])
            unique.append(result)
    unique.sort(key=lambda item: (
        0 if is_file_url(item["url"]) else 1,
        0 if "genesis" in item["name"].lower() else 1,
        0 if "ipfs" in item["name"].lower() else 1,
        item["name"].lower(),
    ))
    return unique


def do_resolve(identifier, mirror_url="https://annas-archive.org"):
    deadline = time.monotonic() + 20
    candidates = []
    for root in mirror_roots(mirror_url):
        for detail_url in detail_urls(identifier, root):
            if time.monotonic() >= deadline:
                return crawl_downloads(candidates, deadline)
            try:
                response = fetch(detail_url, deadline)
            except Exception:
                continue
            if response.status >= 400:
                continue
            if is_media_response(response):
                return [{"name": link_name(response.url), "url": response.url}]
            discovered = [
                (url, name, 0, response.url)
                for url, name in page_links(decode_html(response), response.url)]
            candidates.extend(discovered)
            resolved = crawl_downloads(
                discovered,
                min(deadline, time.monotonic() + 5))
            if resolved:
                return resolved
    return crawl_downloads(candidates, deadline)


def main(argv):
    if len(argv) < 3:
        print(json.dumps({"error": "usage: book_scraper.py search|resolve <argument> [mirror_url]"}))
        return 1
    mode = argv[1].lower()
    argument = argv[2]
    mirror = argv[3] if len(argv) > 3 else "https://annas-archive.org"
    if mode == "search":
        try:
            output = do_search(argument, mirror)
        except TimeoutError:
            output = {"status": "timed_out", "error": "Book search timed out"}
        except Exception:
            output = {"status": "unavailable", "error": "Book search unavailable"}
    elif mode == "resolve":
        output = do_resolve(argument, mirror)
    else:
        output = {"error": f"unknown mode: {mode}"}
    print(json.dumps(output, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
