# Signed provider URLs in app.log — October 23, 2026

Follow-up to the Catalog DASH/TV run on October 23. In that run `app.log` recorded 5 of 6 provider manifest URLs ("Proxy validation OK manifest=…") with their signed path segments intact. Branch **codex/app-recovery** after **e744dcd**.

**Result: the redaction helper was already on every path to `app.log`, but it recognized only one shape of signed path.** It now recognizes the shapes the providers use, both implementations are tested against one shared set of cases, and the scraper no longer shortens a URL before redacting it.

## Where URLs reach app.log

Every line goes through `AppLogger.Log`, which passes it to `LogSanitizer.RedactSensitiveUrls` before writing. No other code writes `app.log`. `TripleNetHandoff` and `ScraperEngine`'s stderr reader redact first as well, so a scraper line is redacted three times. Of the Python scrapers, only `scraper.py` logs URLs. It redacts its own stderr with `redact_log_urls`, which mirrors the C# rules. `audiovisual_scraper.py` and `book_scraper.py` write no URLs to stderr.

Both helpers already removed credentials, whole query strings and fragments. For the path, they redacted a segment only when the whole segment was 32+ hex characters, or 40+ base64url characters mixing upper case, lower case and digits. Real provider routes break that rule in several ways. Run through the old helper, 8 of 9 realistic signed shapes were logged unchanged (only the signed query was redacted):

| Shape | Example (synthetic) | Old result |
| --- | --- | --- |
| Base64 blob with dot-separated signature parts | `/pl/H4sI….MTA4MjMyNDU.ZmUw…/master.m3u8` | logged unchanged |
| Padded base64 | `/stream_new2/H4sI…Ug==/list.m3u8` | logged unchanged |
| Edge-auth token | `/hdntl=exp=…~acl=%2f*~hmac=…/index.m3u8` | logged unchanged |
| Matrix signature | `/h/list;15a38634…cab06.m3u8` | logged unchanged |
| JWT | `/hls/eyJhbGci….eyJleHAi….c2ln…/master.m3u8` | logged unchanged |
| Signature or expiry named by the route | `/token/9f2Kq_x7ZpL3/expires/1792700000/` | logged unchanged |
| Encoded upstream URL in a proxy path | `/proxy/https%3A%2F%2F…%3Ftoken%3D…` | logged unchanged |
| Short hex signature | `/hls/5c0e04d8b3c8f5e0/1080/` | logged unchanged |
| Signed query | `master.m3u8?token=…&expires=…` | `?<redacted>` |

The scraper also cut URLs to 120–180 characters *before* redacting, for example `url[:140]` in the "Proxy validation OK" line. A cut-off token is too short for any pattern to recognize, so its first characters were logged.

Redacting twice was not stable either. Python turned `master.m3u8?<redacted>` into `master.m3u8<redacted>`, and C# turned it into `master.m3u8?<redacted><redacted>`.

## Repair

`LogSanitizer.cs` and `scraper.py` follow the same rules. They still keep the scheme, host, port and readable route segments. Query strings and fragments are still replaced as a whole with `?<redacted>` and `#<redacted>`. A path segment is now replaced with `<redacted>` when it:

- contains `=` (`key=value` parameters, base64 padding) or an embedded `://` URL, after percent-decoding;
- follows a route segment that names a credential (`token`, `sig`, `signature`, `hmac`, `key`, `expires`, `exp`, `policy`, `session`, …);
- contains a token-like run of characters, after splitting on `.`, `;`, `~` and similar characters. That is: 16+ hex characters mixing letters and digits (or 32+ of any hex); a `eyJ…` JWT or JSON part; the old 40+ mixed-case rule; or a 20+ character word mixing upper case, lower case and digits that switches between them at least every other character. That last test separates random tokens from camel-case titles such as `NightOfTheLivingDead1968`.

A redacted segment keeps a known media extension (`<redacted>.m3u8`) so the log still shows what kind of request it was. A lone `?` or `#` left by an earlier pass is kept, so redacting again leaves the line unchanged. `scraper.py` adds `log_url(url, limit)`, which redacts and then shortens. All 15 log lines that shortened a URL inline now use it.

Readable routes are unchanged: embed paths (`/embed/tv/tt0903747/1/1`), slugs, Archive identifiers, numeric IDs, rendition lists (`abcd1234_,l,n,h,.urlset`) and file names.

## Tests

- `tools/tests/fixtures/log-url-redaction.json`: the shared contract. It holds 15 messages, each with its exact expected text and the secrets that must not survive (10 signed shapes, 5 readable routes).
- `DiagnosticLogRedactionTests.LogSanitizer_MatchesSharedSignedProviderUrlContract` (C#) and `test_log_url_redaction.py` (Python) check every case, and check that redacting the result again leaves it unchanged.
- `test_proxy_validation_log_line_hides_signed_manifest_and_segment` runs `validate_hls_result` against a signed master playlist and an edge-auth segment, and reads the line the scraper writes to stderr.
- `test_long_url_is_redacted_before_the_log_line_shortens_it` logs an unsafe URL whose token straddles the 160-character cut.

**Before the repair**, C# failed 10 of the 19 tests in `DiagnosticLogRedactionTests`: 9 contract shapes and the double-redaction check. Python failed 12 subtests in 4 tests.

**After the repair:**

- `run-e2e-tests.ps1 -Filter` over `DiagnosticLogRedactionTests`, `ScraperUnicodeTransportTests`, `AutomaticResolutionRegressionTests`, `ScraperAudiovisualNativeTests`, `RefactoredServicesTests` and `ChallengerBootstrapperTests`: **105 / 105 passed**, 19 of them redaction tests. These are the non-UI tests that use the logger, the scraper engines or `TripleNetHandoff`.
- `python -m unittest discover -s tools/tests -p "test_*.py"`: **233 / 233 passed**, 4 of them new.

The real app and the FlaUI tests were not run; no UI changed.

## Not covered

- A URL that appears without its scheme (`//cdn…`), or percent-encoded outside any URL (`url=https%3A%2F%2F…`), is not matched. No log line in the app writes either.
- Short opaque IDs that no route keyword introduces, such as a 12-character signature segment, stay visible. They look the same as ordinary short IDs.
- Older `app.log` files on disk keep whatever they already recorded.
