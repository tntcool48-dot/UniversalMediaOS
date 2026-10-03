"""Bounded AniList synonym fallback must keep provider identity and unit checks."""
from contextlib import ExitStack
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py'
spec = importlib.util.spec_from_file_location('anime_synonym_fallback', SCRIPT)
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

QUERY = 'Hen Zemi'
SYNONYM = 'Hen Semi'
IDS = {'anilist': 8101, 'mal': 7748}
PAGE = 'https://provider.example/watch/hen-semi/ep-1'


class AnimeSynonymFallbackTests(unittest.TestCase):
    def test_one_catalog_synonym_is_selected_only_without_a_trusted_alternate(self):
        self.assertEqual(SYNONYM, scraper.safe_catalog_synonym(QUERY, [QUERY],
            ['Hentai Seiri Seminar', SYNONYM], IDS))
        self.assertIsNone(scraper.safe_catalog_synonym(QUERY, [QUERY, 'Hen Zemi English'],
            [SYNONYM], IDS))
        self.assertIsNone(scraper.safe_catalog_synonym(QUERY, [QUERY], [SYNONYM], {}))

    def test_generic_and_conflicting_synonyms_are_not_search_terms(self):
        self.assertIsNone(scraper.safe_catalog_synonym('Mushoku Tensei Season 3',
            ['Mushoku Tensei Season 3'], ['Mushoku Tensei Season 2', 'Mushoku'], IDS))
        self.assertIsNone(scraper.safe_catalog_synonym(QUERY, [QUERY], ['X' * 141, '変態'], IDS))

    def test_synonym_page_needs_matching_provider_id(self):
        identity = {'url': PAGE, 'heading': SYNONYM + ' Episode 1',
                    'title': SYNONYM + ' Episode 1', 'active_episodes': ['Episode 1']}
        missing = scraper.check_episode_identity(identity, QUERY, '1',
            catalog_ids=IDS, catalog_synonyms=[SYNONYM])
        matched = scraper.check_episode_identity({**identity, 'primary_ids': {'mal': '7748'}},
            QUERY, '1', catalog_ids=IDS, catalog_synonyms=[SYNONYM])
        self.assertEqual('conflict', missing['status'])
        self.assertEqual('consistent', matched['status'])
        self.assertTrue(matched['synonym_match'])
        self.assertTrue(matched['catalog_id_match'])

    def test_matching_id_cannot_override_wrong_episode_or_season(self):
        for heading in (SYNONYM + ' Episode 10', SYNONYM + ' Season 2 Episode 1'):
            with self.subTest(heading=heading):
                identity = {'url': PAGE, 'heading': heading, 'primary_ids': {'mal': '7748'},
                            'active_episodes': ['Episode 1']}
                self.assertEqual('conflict', scraper.check_episode_identity(identity,
                    QUERY, '1', catalog_ids=IDS, catalog_synonyms=[SYNONYM])['status'])

    def test_related_work_synonym_cannot_resolve_another_catalog_id(self):
        # AniList lists Your Name as a synonym for a Suntory promotional anime.
        identity = {'url': 'https://provider.example/watch/your-name/ep-1',
                    'heading': 'Your Name Episode 1', 'active_episodes': ['Episode 1'],
                    'primary_ids': {'mal': '32281'}}
        found = scraper.check_episode_identity(identity, 'Suntory Minami Alps no Tennen Mizu',
            '1', catalog_ids={'anilist': 97962, 'mal': 34700},
            catalog_synonyms=['Your Name'])
        self.assertEqual('conflict', found['status'])
        self.assertTrue(found['catalog_id_conflict'])

    def test_primary_success_adds_no_synonym_search_request(self):
        self.run_resolve([PAGE], [QUERY])

    def test_primary_miss_tries_one_synonym_inside_the_same_site(self):
        self.run_resolve([], [QUERY, SYNONYM])

    def run_resolve(self, primary_links, expected_queries):
        sites = [{'name': 'fixture', 'url': 'https://provider.example',
                  'source': 'everythingmoe', 'score': 1}]
        page = SimpleNamespace(_ums_match_evidence=None)
        native = {'url': 'https://cdn.example/episode-1.m3u8', '_proxy_fetchable': True}

        def search(_site, term, *_args, **_kwargs):
            return primary_links if term == QUERY else [PAGE]

        with ExitStack() as stack:
            stack.enter_context(patch.object(scraper, 'fetch_indexed_sites', return_value=sites))
            stack.enter_context(patch.object(scraper, 'DRISSION_AVAILABLE', True))
            stack.enter_context(patch.object(scraper, 'launch_browser', return_value=page))
            stack.enter_context(patch.object(scraper, 'close_browser'))
            requests = stack.enter_context(patch.object(scraper, 'search_site', side_effect=search))
            extract = stack.enter_context(patch.object(scraper, 'extract_from_mirror', return_value=native))
            stack.enter_context(patch.object(scraper, 'episode_url_candidates', return_value=[PAGE]))
            result = scraper.do_resolve(QUERY, '1', 1, 'sub', 35,
                catalog_ids=IDS, catalog_synonyms=['Hentai Seiri Seminar', SYNONYM])
        self.assertIs(result, native)
        self.assertEqual(expected_queries, [call.args[1] for call in requests.call_args_list])
        expected_synonyms = None if len(expected_queries) == 1 else [SYNONYM]
        self.assertEqual(expected_synonyms, extract.call_args.kwargs['catalog_synonyms'])

    def test_non_searchable_native_title_fails_fast_without_synonym(self):
        with patch.object(scraper, 'fetch_indexed_sites') as indexes:
            result = scraper.do_resolve('旅人の日記', '1', 1, catalog_ids=IDS)
        self.assertEqual('no_searchable_title', result['error'])
        indexes.assert_not_called()

    def test_native_only_title_uses_catalog_synonym_as_first_search(self):
        native_title = '旅人の日記'
        alternate = "A Traveller's Diary"
        sites = [{'name': 'fixture', 'url': 'https://provider.example',
                  'source': 'everythingmoe', 'score': 1}]
        with ExitStack() as stack:
            stack.enter_context(patch.object(scraper, 'fetch_indexed_sites', return_value=sites))
            stack.enter_context(patch.object(scraper, 'DRISSION_AVAILABLE', True))
            stack.enter_context(patch.object(scraper, 'launch_browser',
                return_value=SimpleNamespace(_ums_match_evidence=None)))
            stack.enter_context(patch.object(scraper, 'close_browser'))
            search = stack.enter_context(patch.object(scraper, 'search_site', return_value=[PAGE]))
            extract = stack.enter_context(patch.object(scraper, 'extract_from_mirror',
                return_value={'url': 'https://cdn.example/episode-1.m3u8'}))
            stack.enter_context(patch.object(scraper, 'episode_url_candidates', return_value=[PAGE]))
            scraper.do_resolve(native_title, '1', 1, catalog_ids=IDS,
                catalog_synonyms=[alternate])
        search.assert_called_once()
        self.assertEqual(alternate, search.call_args.args[1])
        self.assertEqual(native_title, extract.call_args.kwargs['query'])
        self.assertEqual([alternate], extract.call_args.kwargs['catalog_synonyms'])


if __name__ == '__main__':
    unittest.main()
