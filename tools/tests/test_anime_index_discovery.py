"""Offline regressions for automatic anime provider discovery; no browser/network."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py'
spec = importlib.util.spec_from_file_location('anime_scraper', SCRIPT)
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)


def site(name, source='everythingmoe'):
    return {'name': name, 'url': f'https://{name}.example', 'source': source,
            'score': 100, 'rank': 1, 'tags': []}


def index_html(name):
    return (f'<div data-rank="1" data-filter="Dub friendly" class="section-item">'
            f'<a href="/site/{name}" data-link="https://{name}.example"><img alt="">{name}</a></div>')


class AnimeIndexDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='UniversalMediaOS.AnimeIndex.')
        self.addCleanup(self.directory.cleanup)
        self.cache = Path(self.directory.name) / 'streaming-sites.json'
        for name, value in [('SCRAPER_CACHE_DIR', self.directory.name), ('INDEX_CACHE_PATH', str(self.cache))]:
            override = patch.object(scraper, name, value)
            override.start()
            self.addCleanup(override.stop)

    def cache_sites(self, sites, stale=False):
        self.cache.write_text(json.dumps(sites), encoding='utf-8')
        if stale:
            age = time.time() - scraper.INDEX_CACHE_MAX_AGE_SECONDS - 60
            os.utime(self.cache, (age, age))

    def test_fresh_cache_reuses_index_entries_without_old_fixed_seeds(self):
        self.cache_sites([site('current'), site('old-seed', 'seed')])
        with patch.object(scraper, 'fetch_text', side_effect=AssertionError('fresh cache must avoid network')):
            self.assertEqual([site('current')], scraper.fetch_indexed_sites())

    def test_refresh_replaces_old_domains_even_if_only_one_new_site_is_available(self):
        self.cache_sites([site('retired')], stale=True)
        def fetch(url, timeout):
            return (200, url, index_html('replacement')) if 'everythingmoe' in url else (503, url, '')
        with patch.object(scraper, 'fetch_text', side_effect=fetch):
            result = scraper.fetch_indexed_sites()
        self.assertEqual(['replacement'], [entry['name'] for entry in result])
        self.assertEqual(result, json.loads(self.cache.read_text(encoding='utf-8')))

    def test_index_outage_uses_last_discovered_sites_without_fixed_seeds(self):
        self.cache_sites([site('saved', 'theindex'), site('old-seed', 'seed')], stale=True)
        with patch.object(scraper, 'fetch_text', return_value=(503, '', '')):
            self.assertEqual([site('saved', 'theindex')], scraper.fetch_indexed_sites())

    def test_first_run_outage_does_not_invent_provider_domains(self):
        with patch.object(scraper, 'fetch_text', return_value=(503, '', '')):
            self.assertEqual([], scraper.fetch_indexed_sites())
        self.assertFalse(self.cache.exists())

    def test_seed_only_legacy_cache_does_not_hide_a_live_refresh(self):
        self.cache_sites([site('old-seed', 'seed')])
        def fetch(url, timeout):
            return (200, url, index_html('new-domain')) if 'everythingmoe' in url else (503, url, '')
        with patch.object(scraper, 'fetch_text', side_effect=fetch):
            self.assertEqual(['new-domain'], [entry['name'] for entry in scraper.fetch_indexed_sites()])

    def test_malformed_indexes_preserve_last_discovered_sites(self):
        self.cache_sites([site('saved')], stale=True)
        with patch.object(scraper, 'fetch_text', return_value=(200, '', '<html>unavailable</html>')):
            self.assertEqual([site('saved')], scraper.fetch_indexed_sites())

    def test_cache_rejects_local_entries_and_unknown_provenance(self):
        private = site('private') | {'url': 'http://127.0.0.1'}
        self.cache_sites([private, site('unattributed', 'unknown'), site('public')])
        self.assertEqual([site('public')], scraper.load_index_cache())

    def test_both_live_indexes_contribute_without_duplicate_domains(self):
        payload = {'props': {'pageProps': {'columns': [], 'items': [
            {'name': 'Anime replacement', 'urls': ['https://replacement.example']},
            {'name': 'Anime secondary', 'urls': ['https://secondary.example']}
        ]}}}
        other_html = '<script id="__NEXT_DATA__" type="application/json">' + json.dumps(payload) + '</script>'
        def fetch(url, timeout):
            return (200, url, index_html('replacement') if 'everythingmoe' in url else other_html)
        with patch.object(scraper, 'fetch_text', side_effect=fetch):
            result = scraper.fetch_indexed_sites()
        self.assertEqual({'https://replacement.example', 'https://secondary.example'}, {item['url'] for item in result})
        self.assertEqual({'everythingmoe', 'theindex'}, {item['source'] for item in result})
        self.assertEqual(2, len(result))

    def test_no_index_sites_avoids_browser_startup(self):
        with patch.object(scraper, 'fetch_indexed_sites', return_value=[]), patch.object(scraper, 'launch_browser') as browser:
            self.assertEqual('no_indexed_sites', scraper.do_resolve('Fixture', '3', 2)['error'])
            browser.assert_not_called()

    def test_sub_and_dub_rotate_across_the_current_index_pool(self):
        for audio in ('sub', 'dub'):
            with self.subTest(audio=audio):
                selected = [site('unavailable'), site('replacement')]
                def search(entry, query, episode, **kwargs):
                    return [] if entry['name'] == 'unavailable' else ['https://replacement.example/watch/fixture-3']
                with patch.object(scraper, 'fetch_indexed_sites', return_value=selected), \
                     patch.object(scraper, 'DRISSION_AVAILABLE', True), \
                     patch.object(scraper, 'launch_browser', return_value=object()), \
                     patch.object(scraper, 'close_browser'), \
                     patch.object(scraper, 'search_site', side_effect=search) as search_call, \
                     patch.object(scraper, 'browser_search_site', return_value=[]), \
                     patch.object(scraper, 'episode_url_candidates', side_effect=lambda url, episode: [url]), \
                     patch.object(scraper, 'extract_from_mirror', return_value={'url': 'https://media.example/episode-3.m3u8'}) as extract:
                    result = scraper.do_resolve('Fixture', '3', 2, audio)
                self.assertEqual('replacement', result['site'])
                self.assertEqual(2, result['attempted_sites'])
                self.assertEqual(selected, [call.args[0] for call in search_call.call_args_list])
                self.assertEqual(audio, extract.call_args.kwargs['audio_preference'])


if __name__ == '__main__':
    unittest.main()
