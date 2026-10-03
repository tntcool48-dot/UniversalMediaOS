"""Bounded, keyless metadata sample. This does not test playable sources.

python tools/qualify-tvmaze-default.py --output <directory>
API documentation and attribution: https://www.tvmaze.com/api
"""
import argparse
import json
from pathlib import Path
import time
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    samples = [(q, "search/shows?q=" + urllib.parse.quote(q)) for q in
               ("The Office", "Breaking Bad", "Dark", "Severance", "Avatar", "One Piece")]
    samples += [("index-0", "shows?page=0"), ("index-1", "shows?page=1"),
                ("office-episodes", "shows/526/episodes?specials=1")]
    results = []
    for name, path in samples:
        started = time.monotonic()
        result = {"sample": name, "url": "https://api.tvmaze.com/" + path}
        try:
            request = urllib.request.Request(result["url"], headers={
                "User-Agent": "UniversalMediaOS/metadata-qualification", "Accept": "application/json"})
            with urllib.request.urlopen(request, timeout=12) as response:
                data = response.read(4 * 1024 * 1024 + 1)
                if len(data) > 4 * 1024 * 1024:
                    raise ValueError("Response exceeds 4 MB budget")
                rows = json.loads(data)
            if not isinstance(rows, list):
                raise ValueError("Expected an array")
            shows = [row.get("show", row) for row in rows]
            result.update(count=len(shows), posters=sum(bool(row.get("image")) for row in shows),
                          items=[{key: row.get(key) for key in
                                  ("id", "name", "type", "premiered", "season", "number", "url", "image")}
                                 for row in shows])
        except (OSError, ValueError, KeyError, TypeError) as error:
            result["error"] = str(error)
        result["seconds"] = round(time.monotonic() - started, 3)
        results.append(result)
        print(json.dumps({k: v for k, v in result.items() if k != "items"}))
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "tvmaze-selection.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return int(any("error" in result for result in results))


if __name__ == "__main__":
    raise SystemExit(main())
