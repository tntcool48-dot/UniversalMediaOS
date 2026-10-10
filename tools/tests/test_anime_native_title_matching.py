"""Exact native titles can reach indexed lookup without aliases or catalog IDs."""
from contextlib import ExitStack
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('anime_native_titles',
    Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py')
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

QUERY = '葬送のフリーレン'
BASE = 'https://provider.example/search'
PAGE = 'https://provider.example/watch/AbCd1234/ep-1'


class AnimeNativeTitleMatchingTests(unittest.TestCase):
    def identity(self, **changes):
        value = {'url': PAGE, 'heading': QUERY, 'title': QUERY + ' Episode 1',
                 'active_episodes': ['Episode 1']}
        value.update(changes)
        return value

    def test_alias_free_id_free_indexed_request_reaches_independently_matched_native_media(self):
        site = {'name': 'indexed', 'url': 'https://provider.example',
                'source': 'everythingmoe', 'score': 1}
        page = Mock(html='<h1>' + QUERY + '</h1>', url=PAGE)
        page.get.return_value = True
        page.run_js.return_value = self.identity()
        native = {'url': 'https://cdn.example/episode.m3u8', '_proxy_fetchable': True,
                  'audio_languages': [], 'selected_audio': 'sub'}
        html = '<a href="' + PAGE + '">' + QUERY + '</a>'
        with ExitStack() as stack:
            indexes = stack.enter_context(patch.object(scraper, 'fetch_indexed_sites', return_value=[site]))
            stack.enter_context(patch.object(scraper, 'DRISSION_AVAILABLE', True))
            stack.enter_context(patch.object(scraper, 'launch_browser', return_value=page))
            close = stack.enter_context(patch.object(scraper, 'close_browser'))
            fetch = stack.enter_context(patch.object(scraper, 'fetch_text', return_value=(200, BASE, html)))
            for name in ('setup_worker_hooks', 'set_navigation_referer', 'stop_media_listener'):
                stack.enter_context(patch.object(scraper, name))
            stack.enter_context(patch.object(scraper, 'start_media_listener', return_value=False))
            stack.enter_context(patch.object(scraper, 'player_page_unavailable', return_value=False))
            stack.enter_context(patch.object(scraper, 'wait_for_dynamic_player', return_value={}))
            stack.enter_context(patch.object(scraper, 'stage_b_network_sniff', return_value=native))
            result = scraper.do_resolve(QUERY, '1', 1, budget_seconds=35)
        self.assertIs(result, native)
        indexes.assert_called_once()
        self.assertEqual(1, fetch.call_count)
        self.assertEqual('consistent', result['match_evidence']['status'])
        self.assertFalse(result['match_evidence']['catalog_id_match'])
        self.assertFalse(result['match_evidence']['alias_match'])
        self.assertEqual([], result['audio_languages'])
        close.assert_called_once_with(page)

    def test_http_and_browser_cards_require_their_own_complete_native_title(self):
        html = '<a href="' + PAGE + '">' + QUERY + ' Episode 1</a>'
        self.assertEqual([PAGE], scraper.extract_candidate_links(html, BASE, QUERY, '1'))
        page = SimpleNamespace(run_js=lambda *_: [{'href': PAGE, 'text': QUERY, 'label': ''}])
        self.assertEqual([PAGE], scraper.extract_browser_anchor_candidate_links(page, BASE, QUERY, '1'))
        for text in ('フリーレン', '新' + QUERY, QUERY + '外伝'):
            with self.subTest(text=text):
                self.assertEqual([], scraper.extract_candidate_links(
                    '<a href="' + PAGE + '">' + text + '</a>', BASE, QUERY, '1'))
        self.assertEqual([], scraper.extract_candidate_links(
            '<a href="' + PAGE + '">別作品</a><h2>' + QUERY + '</h2>', BASE, QUERY, '1'))

    def test_exact_primary_title_and_selected_unit_establish_consistency_without_ids(self):
        found = scraper.check_episode_identity(self.identity(), QUERY, '1')
        self.assertEqual('consistent', found['status'])
        self.assertEqual([1], found['observed_episodes'])
        self.assertFalse(found['catalog_id_match'])

    def test_native_title_in_own_card_attribute_survives_a_duplicate_or_translated_label(self):
        for html in ('<a href="' + PAGE + '" title="' + QUERY + '">' + QUERY + '</a>',
                     '<a href="' + PAGE + '"><img alt="' + QUERY + '">Sousou no Frieren</a>'):
            with self.subTest(html=html):
                self.assertEqual([PAGE], scraper.extract_candidate_links(html, BASE, QUERY, '1'))
        page = SimpleNamespace(run_js=lambda *_: [{'href': PAGE,
            'text': 'Sousou no Frieren', 'label': QUERY}])
        self.assertEqual([PAGE], scraper.extract_browser_anchor_candidate_links(page, BASE, QUERY, '1'))

    def test_wrong_native_primary_heading_overrides_a_matching_page_title_and_id(self):
        found = scraper.check_episode_identity(self.identity(
            heading='薬屋のひとりごと', primary_ids={'mal': '52991'}),
            QUERY, '1', catalog_ids={'mal': 52991})
        self.assertEqual('conflict', found['status'])
        self.assertTrue(found['catalog_id_match'])

    def test_mixed_native_season_title_cannot_match_another_work_from_the_season_number(self):
        found = scraper.check_episode_identity(self.identity(
            heading='薬屋のひとりごと Season 2', title='薬屋のひとりごと Season 2 Episode 1'),
            QUERY + ' Season 2', '1')
        self.assertEqual('conflict', found['status'])
        self.assertEqual(0, scraper.score_candidate_url(PAGE,
            '薬屋のひとりごと Season 2', QUERY + ' Season 2', '1'))

    def test_added_native_sequel_or_special_text_does_not_match_a_primary_title(self):
        for suffix in ('第2期', '外伝', ' Season 2', ' Special'):
            with self.subTest(suffix=suffix):
                found = scraper.check_episode_identity(self.identity(
                    heading=QUERY + suffix, title=QUERY + suffix + ' Episode 1'), QUERY, '1')
                self.assertEqual('conflict', found['status'])

    def test_conflicting_catalog_id_and_unit_still_reject_exact_native_titles(self):
        for changes in ({'primary_ids': {'mal': '16498'}},
                        {'active_episodes': ['Episode 10']},
                        {'active_episodes': ['Episode 1.5']},
                        {'url': PAGE.replace('ep-1', 'ep-10')},
                        {'url': PAGE.replace('AbCd1234', '薬屋のひとりごと')}):
            with self.subTest(changes=changes):
                found = scraper.check_episode_identity(self.identity(**changes), QUERY, '1',
                    catalog_ids={'mal': 52991})
                self.assertEqual('conflict', found['status'])

    def test_missing_independent_title_or_unit_stops_before_native_capture(self):
        for identity in ({'url': PAGE, 'heading': 'Player'},
                         {'url': PAGE, 'heading': QUERY}):
            with self.subTest(identity=identity):
                page = Mock(html='', url=PAGE)
                page.get.return_value = True
                page.run_js.return_value = identity
                with ExitStack() as stack:
                    for name in ('setup_worker_hooks', 'set_navigation_referer', 'stop_media_listener'):
                        stack.enter_context(patch.object(scraper, name))
                    stack.enter_context(patch.object(scraper, 'start_media_listener', return_value=True))
                    stack.enter_context(patch.object(scraper, 'player_page_unavailable', return_value=False))
                    stack.enter_context(patch.object(scraper, 'wait_for_dynamic_player', return_value={}))
                    listen = stack.enter_context(patch.object(scraper, 'listen_for_media'))
                    sniff = stack.enter_context(patch.object(scraper, 'stage_b_network_sniff'))
                    self.assertIsNone(scraper.extract_from_mirror('', PAGE, page, query=QUERY, episode_id='1'))
                listen.assert_not_called()
                sniff.assert_not_called()

    def test_unicode_canonical_equivalence_preserves_the_whole_title(self):
        query = '魔法の旅人ガイド'
        decomposed = '魔法の旅人カ\u3099イト\u3099'
        found = scraper.check_episode_identity(self.identity(
            heading=decomposed, title=decomposed + ' Episode 1'), query, '1')
        self.assertEqual('consistent', found['status'])

    def test_short_or_unbounded_native_queries_remain_unsearchable(self):
        for query in ('旅', '旅人', '旅' * 141, '!?'):
            with self.subTest(query=query), patch.object(scraper, 'fetch_indexed_sites') as indexes:
                self.assertEqual('no_searchable_title', scraper.do_resolve(query, '1', 1)['error'])
                indexes.assert_not_called()


if __name__ == '__main__':
    unittest.main()
