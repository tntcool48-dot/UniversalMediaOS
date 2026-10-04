"""Provider failure and deadline evidence must not become successful zero matches."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    'book_outcomes', Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core' / 'book_scraper.py')
books = importlib.util.module_from_spec(spec)
spec.loader.exec_module(books)


class BookSearchOutcomeTests(unittest.TestCase):
    def response(self, html, status=200):
        return books.Response(status, 'https://books.test/search', {'content-type': 'text/html'}, html.encode())

    def test_successful_empty_page_is_empty(self):
        with patch.object(books, 'fetch', return_value=self.response('<h2>No results found</h2>')):
            self.assertEqual([], books.do_search('missing'))

    def test_http_failure_is_unavailable(self):
        with patch.object(books, 'fetch', return_value=self.response('Blocked', 403)):
            with self.assertRaises(RuntimeError):
                books.do_search('query')

    def test_challenge_page_is_not_zero_results(self):
        with patch.object(books, 'fetch', return_value=self.response('<h2>Just a moment</h2>')):
            with self.assertRaises(RuntimeError):
                books.do_search('query')

    def test_fetch_timeouts_are_reported_as_timeouts(self):
        with patch.object(books, 'fetch', side_effect=TimeoutError('body stalled')):
            with self.assertRaises(TimeoutError):
                books.do_search('query')

    def test_deadline_is_not_empty_results(self):
        with patch.object(books.time, 'monotonic', side_effect=[0, 21]):
            with self.assertRaises(TimeoutError):
                books.do_search('query')

    def test_fast_matching_page_stops_before_failed_optional_routes(self):
        page = '<div class="search-result"><a href="/book/exact"><h3>Exact</h3></a>File: epub</div>'
        with patch.object(books, 'fetch', side_effect=[self.response(page), TimeoutError()]) as fetch:
            self.assertEqual('exact', books.do_search('Exact')[0]['id'])
            self.assertEqual(1, fetch.call_count)

    def test_cli_emits_distinct_timeout_and_unavailable_outcomes(self):
        for error, expected in [(TimeoutError(), 'timed_out'), (RuntimeError(), 'unavailable')]:
            with self.subTest(status=expected), patch.object(books, 'do_search', side_effect=error):
                stdout = io.StringIO()
                with contextlib.redirect_stdout(stdout):
                    self.assertEqual(0, books.main(['book_scraper.py', 'search', 'query']))
                self.assertEqual(expected, json.loads(stdout.getvalue())['status'])

    def test_retired_domains_route_only_to_current_first_party_mirrors(self):
        expected = ['https://annas-archive.gl', 'https://annas-archive.pk', 'https://annas-archive.gd']
        for suffix in ('org', 'cc', 'li', 'se', 'gs'):
            with self.subTest(suffix=suffix):
                self.assertEqual(expected, books.mirror_roots('https://annas-archive.' + suffix))

    def test_current_mirror_keeps_configured_priority(self):
        self.assertEqual(['https://annas-archive.gd', 'https://annas-archive.gl', 'https://annas-archive.pk'],
                         books.mirror_roots('https://annas-archive.gd/'))

    def test_custom_provider_is_preserved_without_invented_alternatives(self):
        self.assertEqual(['https://books.test/custom'], books.mirror_roots('https://books.test/custom/'))

    def test_resolution_challenge_or_request_channel_is_unavailable(self):
        for html, status in [('<h2>DDoS-Guard</h2>', 403),
                             ('<a href="https://t.me/book_requests">Request this book</a>', 200)]:
            with self.subTest(status=status), patch.object(books, 'fetch', return_value=self.response(html, status)):
                with self.assertRaises(RuntimeError):
                    books.do_resolve('exact')

    def test_resolution_timeout_does_not_become_empty_editions(self):
        with patch.object(books, 'fetch', side_effect=TimeoutError('stalled')):
            with self.assertRaises(TimeoutError):
                books.do_resolve('exact')

    def test_resolution_deadline_stops_before_fetch(self):
        with patch.object(books.time, 'monotonic', side_effect=[0, 21]), patch.object(books, 'fetch') as fetch:
            with self.assertRaises(TimeoutError):
                books.do_resolve('exact')
            fetch.assert_not_called()

    def test_resolve_cli_reports_provider_failure_and_timeout(self):
        for error, expected in [(TimeoutError(), 'timed_out'), (RuntimeError(), 'unavailable')]:
            with self.subTest(status=expected), patch.object(books, 'do_resolve', side_effect=error):
                stdout = io.StringIO()
                with contextlib.redirect_stdout(stdout):
                    self.assertEqual(0, books.main(['book_scraper.py', 'resolve', 'exact']))
                self.assertEqual(expected, json.loads(stdout.getvalue())['status'])


if __name__ == '__main__':
    unittest.main()
