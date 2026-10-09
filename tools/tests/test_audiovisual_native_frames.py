"""Regressions for Movie/TV wrappers hiding a working nested native player."""
import importlib.util
from pathlib import Path
import sys
import time
import unittest
from unittest.mock import Mock, patch

CORE = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core'
sys.path.insert(0, str(CORE))
spec = importlib.util.spec_from_file_location('av_native_frames', CORE / 'audiovisual_scraper.py')
av = importlib.util.module_from_spec(spec)
spec.loader.exec_module(av)

ROOT = 'https://wrapper.example/embed/tv/tt0903747/1/1'
MEDIA = 'https://cdn.example/master.m3u8'


class NativeFrameTests(unittest.TestCase):
    def test_verified_resolve_requests_item_evidence_without_duplicate_provider_targets(self):
        with patch.object(av, 'do_extract', return_value={'error': 'all_sources_failed'}) as extract, patch('builtins.print'):
            self.assertEqual(0, av.main(['av.py', 'resolve-verified', ROOT]))
        self.assertEqual((ROOT, 'sub', 34), extract.call_args.args)
        self.assertEqual({'include_alternatives': False, 'require_item_evidence': True}, extract.call_args.kwargs)

    def test_strict_static_native_keeps_searching_for_independent_item_evidence(self):
        native = dict(av.stream_result(MEDIA), media_validated=True)
        matched = dict(native, evidence={'Origin': 'ProviderItem', 'Identity': {'ImdbId': 'tt0903747'},
            'Unit': {'SeasonNumber': 1, 'EpisodeNumber': 1}})
        for required in (False, True):
            with self.subTest(required=required), patch.object(av, 'compatible_mirror_targets', return_value=[]), \
                 patch.object(av, 'static_waterfall', return_value=(native, [ROOT])), \
                 patch.object(av, 'browser_waterfall', return_value=matched) as browser:
                result = av.do_extract(ROOT, include_alternatives=False, require_item_evidence=required)
            self.assertEqual(matched if required else native, result)
            self.assertEqual(1 if required else 0, browser.call_count)

    def test_strict_browser_continues_past_native_without_evidence_and_closes_its_owner(self):
        native = dict(av.stream_result(MEDIA), media_validated=True)
        matched = dict(native, evidence={'Origin': 'ProviderItem', 'Identity': {'ImdbId': 'tt0903747'},
            'Unit': {'SeasonNumber': 1, 'EpisodeNumber': 1}})
        child = 'https://player.example/embed/tv/tt0903747/1/1'
        page = Mock(url=ROOT)
        with patch.object(av, 'launch_browser', return_value=page), patch.object(av, 'close_browser') as close, \
             patch.object(av, 'browser_target', side_effect=[(native, [child], None), (matched, [], None)]) as target:
            result = av.browser_waterfall([ROOT], time.monotonic()+5, require_item_evidence=True)
        self.assertEqual(matched, result)
        self.assertEqual(2, target.call_count)
        close.assert_called_once_with(page)

    def test_strict_browser_exhaustion_retains_unknown_instead_of_inventing_evidence(self):
        native = dict(av.stream_result(MEDIA), media_validated=True)
        page = Mock(url=ROOT)
        with patch.object(av, 'launch_browser', return_value=page), patch.object(av, 'close_browser') as close, \
             patch.object(av, 'browser_target', return_value=(native, [], None)):
            result = av.browser_waterfall([ROOT], time.monotonic()+5, require_item_evidence=True)
        self.assertEqual(native, result)
        self.assertNotIn('evidence', result)
        close.assert_called_once_with(page)

    def test_captions_alone_do_not_stop_a_strict_frame_scan(self):
        first = {'url': MEDIA}
        second = {'url': 'https://cdn.example/second.m3u8'}
        captions_only = dict(first, media_validated=True, subtitles=[{'language': 'en'}])
        matched = dict(second, media_validated=True, evidence={'Origin': 'ProviderItem',
            'Identity': {'ImdbId': 'tt0903747'}, 'Unit': {'SeasonNumber': 1, 'EpisodeNumber': 1}})
        page = Mock(url=ROOT)
        page.get_frames.return_value = []
        with patch.object(av, 'start_listener'), \
             patch.object(av, 'browser_dom_scan', side_effect=[(first, []), (second, [])]), \
             patch.object(av, 'validated_stream', side_effect=lambda candidate, deadline: candidate), \
             patch.object(av, 'enrich_native_result', side_effect=[captions_only, matched]):
            result = av.scan_embedded_players(page, ROOT, time.monotonic()+5, require_item_evidence=True)
        self.assertEqual(matched, result)

    def test_coordinated_resolve_keeps_mirrors_without_duplicating_queued_providers(self):
        mirror, other = 'https://mirror.example/embed/tv/tt0903747/1/1', 'https://other.example/embed/tv/tt0903747/1/1'
        for mode, expected in [('resolve', [ROOT, mirror]), ('extract', [ROOT, other, mirror])]:
            with self.subTest(mode=mode), patch.object(av, 'compatible_mirror_targets', return_value=[mirror]), \
                 patch.object(av, 'embed_alternate_targets', return_value=[other]), \
                 patch.object(av, 'static_waterfall', return_value=(None, [ROOT])) as static, \
                 patch.object(av, 'browser_waterfall', return_value=av.stream_result(MEDIA)), patch('builtins.print'):
                self.assertEqual(0, av.main(['av.py', mode, ROOT]))
                self.assertEqual(expected, static.call_args.args[0])

    def test_script_ad_and_private_links_do_not_enter_player_queue(self):
        markup = '''<script src="https://metrics.example/player.js"></script>
            <iframe src="https://metrics.example/idg/"></iframe>
            <iframe src="http://127.0.0.1/embed/tv/tt0903747/1/1"></iframe>
            <iframe src="https://player.example/embed/tv/tt0903747/1-1?vs=signed"></iframe>
            <a href="/library">Library</a>'''
        self.assertEqual(['https://player.example/embed/tv/tt0903747/1-1?vs=signed'],
                         av.extract_child_pages(markup, ROOT))

    def test_wrong_imdb_kind_season_or_episode_is_not_traversed(self):
        for target in ['https://player.example/embed/movie/tt0903747',
                       'https://player.example/embed/tv/tt0944947/1/1',
                       'https://player.example/embed/tv/TT0944947/1/1',
                       'https://player.example/embed/tv/tt0903747/2-1',
                       'https://player.example/embed/tv/tt0903747/1/2']:
            with self.subTest(target=target):
                self.assertFalse(av.compatible_player_page(target, ROOT))
        self.assertTrue(av.compatible_player_page('https://player.example/embed/player/tv/tt0903747/1-1?vs=signed', ROOT))
        for original in ['https://auto.example/tv/imdb/tt0903747-1-1',
                         'https://multi.example/?video_id=tt0903747&s=1&e=1']:
            self.assertFalse(av.compatible_player_page('https://player.example/embed/movie/tt0903747', original))
        self.assertFalse(av.compatible_player_page('https://multi.example/?video_id=tt0903747&s=bad&e=1', ROOT))

    def test_static_child_is_checked_before_another_root(self):
        child = 'https://player.example/embed/tv/tt0903747/1-1'
        calls = []
        def fetch(url, timeout):
            calls.append(url)
            return (None, [child]) if url == ROOT else (av.stream_result(MEDIA), [])
        with patch.object(av, 'static_page', side_effect=fetch), \
             patch.object(av, 'validated_stream', side_effect=lambda result, deadline: result):
            result, _ = av.static_waterfall([ROOT, 'https://slow.example/embed/tv/tt0903747/1/1'], time.monotonic()+5)
        self.assertEqual(MEDIA, result['url'])
        self.assertEqual([ROOT, child], calls)

    def test_loaded_third_level_frame_is_inspected_without_top_level_navigation(self):
        video = Mock(url='https://player.example/embed/player/tv/tt0903747/1/1?vs=signed')
        landing = Mock(url='https://player.example/embed/tv/tt0903747/1-1?vs=signed')
        wrapper = Mock(url='https://inner.example/embed/tv/tt0903747/1-1')
        root = Mock(url=ROOT)
        root.get_frames.return_value = [wrapper]
        wrapper.get_frames.return_value = [landing]
        landing.get_frames.return_value = [video]
        video.get_frames.return_value = []
        for document in [root, wrapper, landing, video]:
            document.run_js.return_value = [1280, 657]
        def scan(document, referer, rejected=None):
            return (av.stream_result(MEDIA, referer), []) if document is video else (None, [])
        with patch.object(av, 'start_listener'), patch.object(av, 'browser_dom_scan', side_effect=scan), \
             patch.object(av, 'click_player_controls', return_value=None), \
             patch.object(av, 'validated_stream', side_effect=lambda result, deadline: result):
            result = av.scan_embedded_players(root, ROOT, time.monotonic()+10)
        self.assertEqual(video.url, result['referer'])
        for document in [root, wrapper, landing, video]:
            document.get.assert_not_called()

    def test_hidden_frame_and_conflicting_episode_never_produce_a_stream(self):
        hidden = Mock(url='https://player.example/embed/tv/tt0903747/1/1')
        hidden.run_js.return_value = [0, 0]
        wrong = Mock(url='https://player.example/embed/tv/tt0903747/1/2')
        root = Mock(url=ROOT)
        root.get_frames.return_value = [hidden, wrong]
        with patch.object(av, 'start_listener'), patch.object(av, 'browser_dom_scan', return_value=(None, [])) as scan, \
             patch.object(av, 'click_player_controls', return_value=None):
            self.assertIsNone(av.scan_embedded_players(root, ROOT, time.monotonic()+5))
        self.assertEqual([root], [call.args[0] for call in scan.call_args_list])

    def test_unknown_wrapper_keeps_original_unit_for_nested_scan(self):
        page = Mock(url='https://player.example/watch/signed')
        page.html = '<div>Player</div>'
        page.cookies.return_value = {}
        with patch.object(av, 'start_listener'), patch.object(av, 'scan_embedded_players', return_value=None) as scan, \
             patch.object(av, 'browser_dom_scan', return_value=(None, [])):
            av.browser_target(page, page.url, time.monotonic()+5, ROOT)
        self.assertEqual(ROOT, scan.call_args.args[1])

    def test_conflicting_redirect_cannot_become_a_native_or_website_result(self):
        page = Mock(url='https://player.example/embed/tv/tt0903747/1/2')
        with patch.object(av, 'start_listener'), patch.object(av, 'scan_embedded_players') as scan:
            self.assertEqual((None, [], None), av.browser_target(page, ROOT, time.monotonic()+5, ROOT))
        scan.assert_not_called()

    def test_mislabeled_or_blocked_candidate_is_rejected_without_web_success(self):
        with patch.object(av, 'validate_hls_result', return_value=False) as validate:
            self.assertIsNone(av.validated_stream(av.stream_result(MEDIA), time.monotonic()+5))
        self.assertFalse(validate.call_args.kwargs['inspect_alternate_audio'])
        self.assertIn('deadline', validate.call_args.kwargs)
        self.assertIsNone(av.validated_stream(av.stream_result(ROOT, requires_webview=True), time.monotonic()+5))

    def test_direct_media_url_must_pass_byte_check(self):
        with patch.object(av, 'validate_media_payload', return_value=False):
            self.assertEqual('media_validation_failed', av.do_extract('https://cdn.example/blocked.mp4')['error'])

    def test_dash_xml_cannot_be_certified_by_payload_size(self):
        with patch.object(av, 'validate_media_payload', return_value=True) as validate, \
             patch.object(av, 'validate_dash_result', return_value=False):
            self.assertIsNone(av.validated_stream(av.stream_result('https://cdn.example/master.mpd'), time.monotonic()+5))
        validate.assert_not_called()

    def test_expired_operation_does_not_start_validation(self):
        with patch.object(av, 'validate_hls_result') as validate:
            self.assertIsNone(av.validated_stream(av.stream_result(MEDIA), time.monotonic()-1))
        validate.assert_not_called()


if __name__ == '__main__':
    unittest.main()
