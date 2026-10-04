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


if __name__ == '__main__':
    unittest.main()
