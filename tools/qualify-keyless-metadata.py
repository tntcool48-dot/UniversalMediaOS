"""Repeat the bounded IP05 live sample without credentials or browser sessions.

Usage: python tools/qualify-keyless-metadata.py --output <evidence-directory>
This is a qualification tool, not the app's catalog or a playback availability test.
"""
import argparse
import json
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def fetch(url):
    request = urllib.request.Request(url, headers={
        "User-Agent": "UniversalMediaOS-metadata-qualification/1.0",
        "Accept": "application/json",
    })
    with urllib.request.urlopen(request, timeout=12) as response:
        body = response.read(4_000_001)
        if len(body) > 4_000_000:
            raise ValueError("Response exceeded qualification budget")
        return json.loads(body)


def wikidata(query):
    base = "https://www.wikidata.org/w/api.php?"
    search = fetch(base + urllib.parse.urlencode({
        "action": "wbsearchentities", "search": query, "language": "en",
        "format": "json", "type": "item", "limit": 8,
    }))
    ids = [item["id"] for item in search["search"]]
    if not ids:
        return []
    entities = fetch(base + urllib.parse.urlencode({
        "action": "wbgetentities", "ids": "|".join(ids),
        "props": "claims|labels", "languages": "en", "format": "json",
    }))["entities"]
    rows = []
    for identity in ids:
        item = entities[identity]

        def values(property_id):
            return [claim.get("mainsnak", {}).get("datavalue", {}).get("value")
                    for claim in item.get("claims", {}).get(property_id, [])
                    if claim.get("rank") != "deprecated"]

        rows.append({"id": identity, "title": item.get("labels", {}).get("en", {}).get("value"),
                     "types": values("P31"), "imdb": values("P345"),
                     "dates": values("P577"), "genres": values("P136"),
                     "poster_names": values("P3383")})
    return rows


def tvmaze_search(query):
    results = fetch("https://api.tvmaze.com/search/shows?" + urllib.parse.urlencode({"q": query}))
    return [{"id": row["show"]["id"], "title": row["show"]["name"],
             "type": row["show"].get("type"), "premiered": row["show"].get("premiered"),
             "externals": row["show"].get("externals")} for row in results]


def wikidata_typed(query="", offset=0, animated=False):
    base = "https://www.wikidata.org/w/api.php?"
    category = "haswbstatement:P31=Q202866" if animated else "haswbstatement:P31=Q11424|P31=Q202866"
    search = fetch(base + urllib.parse.urlencode({
        "action": "query", "list": "search", "srsearch": f'"{query}" {category}' if query else category,
        "srnamespace": 0, "srprop": "", "srlimit": 5, "sroffset": offset,
        "srsort": "relevance" if query else "incoming_links_desc", "format": "json", "maxlag": 5,
    }))
    ids = [row["title"] for row in search["query"]["search"]]
    entities = fetch(base + urllib.parse.urlencode({
        "action": "wbgetentities", "ids": "|".join(ids), "props": "labels|claims",
        "languages": "en|mul", "format": "json", "maxlag": 5,
    })) if ids else {"entities": {}}
    # Keep the parsing evidence without reference blocks and unrelated claims.
    for entity in entities.get("entities", {}).values():
        entity["claims"] = {prop: [{key: claim[key] for key in ("rank", "mainsnak") if key in claim}
                                   for claim in claims]
                            for prop, claims in entity.get("claims", {}).items()
                            if prop in ("P31", "P577", "P345", "P3383")}
    return {"search": search, "entities": entities}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--typed-films", action="store_true", help="Run the bounded typed film sample")
    args = parser.parse_args()
    cases = [("wikidata-" + query, lambda q=query: wikidata(q))
             for query in ("The Matrix", "Dune", "Toy Story", "Toy Story 2")]
    cases += [("tvmaze-" + query, lambda q=query: tvmaze_search(q))
              for query in ("the office", "avatar")]
    cases += [("tvmaze-index-" + str(page), lambda p=page:
               [show["id"] for show in fetch(f"https://api.tvmaze.com/shows?page={p}")])
              for page in (0, 1)]
    cases += [("tvmaze-office-episodes", lambda:
               [{key: episode.get(key) for key in ("id", "season", "number", "name", "type")}
                for episode in fetch("https://api.tvmaze.com/shows/526/episodes?specials=1")])]
    if args.typed_films:
        cases = [("films-page-1", lambda: wikidata_typed()),
                 ("films-page-2", lambda: wikidata_typed(offset=5)),
                 ("dune", lambda: wikidata_typed("Dune")),
                 ("toy", lambda: wikidata_typed("Toy Story", animated=True))]
    report = []
    for name, action in cases:
        started = time.monotonic()
        row = {"name": name}
        try:
            row["results"] = action()
            row["status"] = "success"
        except urllib.error.HTTPError as error:
            row.update(status="http_error", code=error.code)
        except (urllib.error.URLError, TimeoutError, ValueError, KeyError, TypeError) as error:
            row.update(status="failed", error_type=type(error).__name__)
        row["seconds"] = round(time.monotonic() - started, 2)
        report.append(row)
        print(f"{name}: {row['status']} ({row['seconds']}s)", flush=True)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / ("typed-film-sample.json" if args.typed_films else "qualification-sample.json")).write_text(
        json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
