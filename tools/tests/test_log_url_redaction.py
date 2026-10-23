"""Regressions for signed provider URLs reaching app.log through scraper stderr."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'UniversalMediaOS.Core/scraper.py'
FIXTURE = Path(__file__).resolve().parent / 'fixtures/log-url-redaction.json'
spec = importlib.util.spec_from_file_location('log_redaction_scraper', SCRIPT)
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

SIGNED_MANIFEST = ('https://tmstr4.example.com/pl/'
                   'H4sIAAAAAAAAAw3JwRKCIBAA0F9iA5zxyqQGjhvYxpqOIjUxm.MTA4MjMyNDU.ZmUwYjE5ZmQ/master.m3u8')
SIGNED_SEGMENT_PATH = 'hdntl=exp=1792700000~acl=%2f*~hmac=0a1b2c3d4e5f6a7b8c9d/seg-1-v1-a1.ts'


def playlist(url, text):
    return Mock(status_code=200, headers={'content-type': 'application/vnd.apple.mpegurl'}, url=url, text=text)


def captured_log(action):
    stderr = io.StringIO()
    with contextlib.redirect_stderr(stderr):
        action()
    return stderr.getvalue()


class SharedRedactionContractTests(unittest.TestCase):
    def test_scraper_redactor_matches_the_shared_contract(self):
        cases = json.loads(FIXTURE.read_text(encoding='utf-8'))['cases']
        for case in cases:
            with self.subTest(case['name']):
                safe = scraper.redact_log_urls(case['message'])
                self.assertEqual(case['expected'], safe)
                for secret in case['secrets']:
                    self.assertNotIn(secret, safe)

    def test_redaction_is_stable_when_applied_again(self):
        # The C# side redacts scraper stderr a second time before app.log.
        cases = json.loads(FIXTURE.read_text(encoding='utf-8'))['cases']
        for case in cases:
            with self.subTest(case['name']):
                self.assertEqual(case['expected'], scraper.redact_log_urls(case['expected']))


class ScraperLogLineTests(unittest.TestCase):
    def test_proxy_validation_log_line_hides_signed_manifest_and_segment(self):
        variant = 'https://tmstr4.example.com/pl/variant/index.m3u8'
        segment = 'https://seg.example.net/' + SIGNED_SEGMENT_PATH
        responses = [
            playlist(SIGNED_MANIFEST, '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n' + variant + '\n'),
            playlist(variant, '#EXTM3U\n#EXTINF:4,\n' + segment + '\n'),
        ]
        http = Mock()
        http.get.side_effect = responses
        with patch.object(scraper, 'cffi_req', http), \
                patch.object(scraper, 'is_safe_public_url', return_value=True), \
                patch.object(scraper, 'validate_media_payload', return_value=True):
            log = captured_log(lambda: self.assertTrue(scraper.validate_hls_result(SIGNED_MANIFEST, {})))

        self.assertIn('Proxy validation OK manifest=https://tmstr4.example.com/pl/<redacted>/master.m3u8 '
                      'segment=https://seg.example.net/<redacted>/seg-1-v1-a1.ts', log)
        for secret in ('H4sIAAAAAAAAAw3J', 'MTA4MjMyNDU', 'ZmUwYjE5ZmQ', 'hmac', '0a1b2c3d4e5f6a7b8c9d', '1792700000'):
            self.assertNotIn(secret, log)

    def test_long_url_is_redacted_before_the_log_line_shortens_it(self):
        token = 'Xk3Jd9aLq0ZmP2vR8tYb4NcWuE7hG1sQ5fT2yB6nM0pK9wC4zD8'
        url = 'https://cdn.example.internal/' + 'readable-route-name/' * 6 + token + '/master.m3u8'

        log = captured_log(lambda: self.assertFalse(scraper.is_proxy_fetchable_result({'url': url})))

        self.assertIn('Proxy validation rejected unsafe URL: https://cdn.example.internal/readable-route-name/', log)
        # Shortening first would keep a token stub too short for any pattern to recognize.
        self.assertNotIn(token[:8], log)


if __name__ == '__main__':
    unittest.main()
