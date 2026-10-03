"""Native-first and explicitly selected website regressions; no network/browser."""
from contextlib import ExitStack
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

SCRIPT = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py'
spec = importlib.util.spec_from_file_location('anime_native_policy', SCRIPT)
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

QUERY = 'Fixture Season 2'
EPISODE = '7'
PARENT = 'https://provider.example/fixture-season-2/episode-7'
EVIDENCE = {'status': 'consistent', 'observed_episodes': [7], 'observed_seasons': [2]}


def native():
    return {'url': 'https://cdn.example/master.m3u8', '_proxy_fetchable': True,
            'selected_audio': 'dub', 'audio_languages': ['eng'],
            'validated_hls_variant': 'https://cdn.example/1080.m3u8',
            'subtitles': [{'url': 'https://cdn.example/en.vtt', 'language': 'en'}]}


def blocked():
    return {'url': 'https://cdn.example/blocked.m3u8', 'referer': PARENT,
            '_proxy_fetchable': False}


def website():
    return {'url': PARENT, 'referer': PARENT, 'requires_webview': True,
            'match_evidence': dict(EVIDENCE)}


class AnimeNativePolicyTests(unittest.TestCase):
    def resolve(self, extract, *, links=None, prefer_native=True, limit=0, clock=None, close=None):
        sites = [{'name': name, 'url': f'https://{name}.example', 'source': 'everythingmoe'}
                 for name in ('first', 'later')]
        page = SimpleNamespace(_ums_match_evidence=dict(EVIDENCE))
        with ExitStack() as stack:
            for name, value in [('fetch_indexed_sites', sites), ('DRISSION_AVAILABLE', True),
                                ('launch_browser', page)]:
                stack.enter_context(patch.object(scraper, name, return_value=value) if callable(getattr(scraper, name))
                                    else patch.object(scraper, name, value))
            closed = stack.enter_context(patch.object(scraper, 'close_browser', close)) if close else \
                stack.enter_context(patch.object(scraper, 'close_browser'))
            stack.enter_context(patch.object(scraper, 'search_site',
                side_effect=links or (lambda site, *args, **kwargs: [site['url'] + '/episode-7'])))
            stack.enter_context(patch.object(scraper, 'browser_search_site', return_value=[]))
            stack.enter_context(patch.object(scraper, 'episode_url_candidates', side_effect=lambda url, episode: [url]))
            extractor = stack.enter_context(patch.object(scraper, 'extract_from_mirror', side_effect=extract))
            if clock is not None:
                stack.enter_context(patch.object(scraper.time, 'monotonic', side_effect=lambda: clock[0]))
            result = scraper.do_resolve(QUERY, EPISODE, limit, 'sub', 110, prefer_native=prefer_native)
        return result, extractor, closed

    def test_browser_only_payload_is_saved_without_becoming_native_success(self):
        saved = []
        self.assertIsNone(scraper.accept_stream_result(blocked(), 'Initial network', browser_fallbacks=saved))
        self.assertTrue(saved[0]['requires_webview'])
        result = native()
        self.assertIs(result, scraper.accept_stream_result(result, 'Next server', browser_fallbacks=saved))
        self.assertEqual(1, len(saved))
        self.assertEqual('eng', result['audio_languages'][0])
        self.assertEqual('en', result['subtitles'][0]['language'])

    def test_failed_or_known_wrong_dub_cannot_be_saved_as_a_website_success(self):
        for result in (blocked(), native() | {'audio_languages': ['jpn']}):
            with self.subTest(fetchable=result['_proxy_fetchable']):
                saved = []
                self.assertIsNone(scraper.accept_stream_result(result, 'Dub', 'dub', saved))
                self.assertEqual([], saved)

    def test_iframe_network_failure_still_tries_its_dom_media(self):
        page = Mock(html='', url=PARENT)
        page.get.return_value = True
        saved, result = [], native()
        with patch.object(scraper, 'start_media_listener', return_value=True), \
             patch.object(scraper, 'stop_media_listener'), \
             patch.object(scraper, 'player_page_unavailable', return_value=False), \
             patch.object(scraper, 'listen_for_media', return_value=blocked()), \
             patch.object(scraper, 'extract_dom_media_urls', return_value=[result['url']]), \
             patch.object(scraper, 'make_stream_result', return_value=result):
            actual = scraper.extract_player_page(page, 'https://player.example/embed/7', PARENT,
                browser_fallbacks=saved)
        self.assertIs(result, actual)
        self.assertTrue(saved[0]['requires_webview'])

    def test_browser_only_first_iframe_does_not_hide_a_later_native_frame(self):
        page = Mock(url=PARENT)
        page._ums_selected_audio_frames = {}
        result, saved = native(), []
        with patch.object(scraper, 'collect_iframe_sources',
                return_value=['https://cdn.example/one.mp4', 'https://cdn.example/two.mp4']), \
             patch.object(scraper, 'make_stream_result', side_effect=[blocked(), result]):
            actual = scraper.stage_c_iframe_crawl(page, parent_url=PARENT, browser_fallbacks=saved)
        self.assertIs(result, actual)
        self.assertEqual(1, len(saved))

    def test_parent_browser_capture_still_tries_the_selected_native_server(self):
        page = Mock(html='', url=PARENT)
        page.get.return_value = True
        def identity(*args):
            page._ums_match_evidence = dict(EVIDENCE)
            return True
        result, saved = native(), []
        with patch.object(scraper, 'setup_worker_hooks'), patch.object(scraper, 'set_navigation_referer'), \
             patch.object(scraper, 'start_media_listener', return_value=True), \
             patch.object(scraper, 'stop_media_listener'), \
             patch.object(scraper, 'player_page_unavailable', return_value=False), \
             patch.object(scraper, 'wait_for_dynamic_player', return_value={'video': True}), \
             patch.object(scraper, 'read_episode_identity', side_effect=identity), \
             patch.object(scraper, 'listen_for_media', return_value=blocked()), \
             patch.object(scraper, 'stage_b_network_sniff', return_value=result):
            actual = scraper.extract_from_mirror('', PARENT, page, query=QUERY, episode_id=EPISODE,
                browser_fallbacks=saved)
        self.assertIs(result, actual)
        self.assertEqual(EVIDENCE, saved[0]['match_evidence'])

    def test_rotated_wrong_episode_cannot_overwrite_a_saved_pages_matching_evidence(self):
        page = Mock(html='', url=PARENT)
        page.get.return_value = True
        identities = iter([dict(EVIDENCE), {'status': 'conflict', 'observed_episodes': [8]}])
        def identity(*args):
            page._ums_match_evidence = next(identities)
            return page._ums_match_evidence['status'] == 'consistent'
        def controls(*args, **kwargs):
            kwargs['excluded'].add('first-server')
            return None
        saved = []
        with patch.object(scraper, 'setup_worker_hooks'), patch.object(scraper, 'set_navigation_referer'), \
             patch.object(scraper, 'start_media_listener', return_value=True), \
             patch.object(scraper, 'stop_media_listener'), \
             patch.object(scraper, 'player_page_unavailable', return_value=False), \
             patch.object(scraper, 'wait_for_dynamic_player', return_value={'video': True}), \
             patch.object(scraper, 'read_episode_identity', side_effect=identity), \
             patch.object(scraper, 'listen_for_media', return_value=blocked()), \
             patch.object(scraper, 'stage_b_network_sniff', side_effect=controls), \
             patch.object(scraper, 'stage_d_carpet_bomb', return_value=None), \
             patch.object(scraper, 'stage_a_player_fingerprint', return_value=None), \
             patch.object(scraper, 'stage_c_iframe_crawl', return_value=None):
            actual = scraper.extract_from_mirror('', PARENT, page, query=QUERY, episode_id=EPISODE,
                browser_fallbacks=saved)
        self.assertIsNone(actual)
        self.assertEqual(EVIDENCE, saved[0]['match_evidence'])
        self.assertEqual('conflict', page._ums_match_evidence['status'])

    def test_current_index_later_native_provider_beats_a_browser_only_provider(self):
        result, extract, close = self.resolve([website(), native()])
        self.assertFalse(result.get('requires_webview', False))
        self.assertEqual('later', result['site'])
        self.assertEqual(2, result['attempted_sites'])
        self.assertEqual(2, extract.call_count)
        for call in extract.call_args_list:
            self.assertEqual(QUERY, call.kwargs['query'])
            self.assertEqual(EPISODE, call.kwargs['episode_id'])
            self.assertEqual('sub', call.kwargs['audio_preference'])
            self.assertIsNotNone(call.kwargs['browser_fallbacks'])
        close.assert_called_once()

    def test_saved_extraction_fallback_does_not_stop_provider_rotation(self):
        def extract(mirror, candidate, page, **kwargs):
            if 'first.example' in candidate:
                kwargs['browser_fallbacks'].append(website())
                return None
            return native()
        result, _, _ = self.resolve(extract)
        self.assertEqual('later', result['site'])
        self.assertFalse(result.get('requires_webview', False))

    def test_later_candidate_on_the_same_provider_can_supply_native_media(self):
        links = lambda *args, **kwargs: [PARENT + '?server=1', PARENT + '?server=2']
        result, extract, _ = self.resolve([website(), native()], links=links)
        self.assertEqual('first', result['site'])
        self.assertEqual(1, result['attempted_sites'])
        self.assertEqual(2, extract.call_count)

    def test_exhaustion_returns_only_browser_availability_with_its_original_evidence(self):
        result, extract, close = self.resolve([website(), None])
        self.assertTrue(result['requires_webview'])
        self.assertEqual(PARENT, result['url'])
        self.assertEqual('first', result['site'])
        self.assertEqual(EVIDENCE, result['match_evidence'])
        self.assertEqual(2, result['attempted_sites'])
        self.assertEqual(2, extract.call_count)
        close.assert_called_once()

    def test_explicit_website_choice_does_not_wait_for_later_native_providers(self):
        result, extract, close = self.resolve([website(), native()], prefer_native=False)
        self.assertTrue(result['requires_webview'])
        self.assertEqual(1, extract.call_count)
        self.assertIsNone(extract.call_args.kwargs['browser_fallbacks'])
        close.assert_called_once()

    def test_native_lookup_keeps_the_existing_site_limit(self):
        result, extract, _ = self.resolve([website(), native()], limit=1)
        self.assertTrue(result['requires_webview'])
        self.assertEqual(1, result['attempted_sites'])
        self.assertEqual(1, extract.call_count)

    def test_browser_candidate_does_not_extend_the_native_global_deadline(self):
        clock = [100.0]
        def extract(*args, **kwargs):
            clock[0] += 111
            return website()
        result, extract_call, close = self.resolve(extract, clock=clock)
        self.assertTrue(result['requires_webview'])
        self.assertEqual(1, extract_call.call_count)
        close.assert_called_once()

    def test_interrupted_native_lookup_closes_browser_without_returning_a_website(self):
        closed = Mock()
        with self.assertRaises(KeyboardInterrupt):
            self.resolve(KeyboardInterrupt(), close=closed)
        closed.assert_called_once()


if __name__ == '__main__':
    unittest.main()
