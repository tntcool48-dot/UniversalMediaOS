"""Catalog-title aliases may find the same anime without relaxing unit/audio checks."""
from contextlib import ExitStack
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py'
spec = importlib.util.spec_from_file_location('anime_alias_matching', SCRIPT)
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

ENGLISH = "Frieren: Beyond Journey's End"
ROMAJI = 'Sousou no Frieren'
EPISODE_PAGE = 'https://provider.example/watch/sousou-no-frieren/ep-1'


class AnimeAliasMatchingTests(unittest.TestCase):
    def test_english_search_page_can_contain_only_the_same_shows_romaji_title(self):
        html = f'<a href="{EPISODE_PAGE}">{ROMAJI} Episode 1</a>'
        self.assertFalse(scraper.html_is_relevant(html, ENGLISH))
        self.assertTrue(scraper.html_is_relevant(html, ENGLISH, [ROMAJI]))
        self.assertEqual([EPISODE_PAGE], scraper.extract_candidate_links(
            html, 'https://provider.example/search', ENGLISH, '1', [ROMAJI]))

    def test_browser_card_with_matching_romaji_alias_is_eligible(self):
        page = SimpleNamespace(run_js=lambda *_: [{'href': EPISODE_PAGE,
            'text': ROMAJI + ' Episode 1', 'label': ''}])
        self.assertEqual([EPISODE_PAGE], scraper.extract_browser_anchor_candidate_links(
            page, 'https://provider.example', ENGLISH, '1', [ROMAJI]))

    def test_unrelated_watch_card_stays_rejected_even_if_its_label_uses_an_alias(self):
        wrong = 'https://provider.example/watch/attack-on-titan/ep-1'
        self.assertEqual(0, scraper.score_candidate_url(wrong, ROMAJI, ENGLISH, '1', [ROMAJI]))

    def test_non_matching_aliases_cannot_transfer_a_different_season_or_movie(self):
        for query, alias in [
            ('Mushoku Tensei Season 3', 'Mushoku Tensei Season 2'),
            ('Mushoku Tensei Season 3', 'Mushoku Tensei'),
            ('Vinland Saga', 'Vinland Saga Movie'),
            ('Vinland Saga Part 2', 'Vinland Saga Part 1'),
        ]:
            with self.subTest(query=query, alias=alias):
                self.assertEqual([query], scraper.safe_title_aliases(query, [alias]))

    def test_generic_one_word_alias_does_not_expand_a_specific_title(self):
        self.assertEqual([ENGLISH], scraper.safe_title_aliases(ENGLISH, ['Frieren']))
        self.assertEqual(0, scraper.score_candidate_url(
            'https://provider.example/watch/frieren/ep-1', 'Frieren', ENGLISH, '1', ['Frieren']))

    def test_duplicate_and_long_aliases_do_not_change_matching(self):
        self.assertEqual([ENGLISH, ROMAJI], scraper.safe_title_aliases(
            ENGLISH, [ENGLISH.upper(), ROMAJI, 'Extra Name']))
        self.assertEqual([ENGLISH], scraper.safe_title_aliases(ENGLISH, ['X' * 141]))

    def test_romaji_primary_heading_keeps_english_catalog_identity_consistent(self):
        identity = {'url': EPISODE_PAGE, 'heading': ROMAJI + ' Episode 1',
                    'title': ROMAJI + ' Episode 1', 'active_episodes': ['Episode 1']}
        without = scraper.check_episode_identity(identity, ENGLISH, '1')
        with_alias = scraper.check_episode_identity(identity, ENGLISH, '1', [ROMAJI])
        self.assertEqual('conflict', without['status'])
        self.assertEqual('consistent', with_alias['status'])
        self.assertTrue(with_alias['alias_match'])
        self.assertEqual([1], with_alias['observed_episodes'])

    def test_alias_cannot_revive_a_wrong_primary_heading_or_episode(self):
        for heading, expected_episode in [
            ('Attack on Titan Episode 1', '1'),
            (ROMAJI + ' Episode 10', '1'),
            (ROMAJI + ' Season 2 Episode 1', '1'),
        ]:
            with self.subTest(heading=heading):
                identity = {'url': EPISODE_PAGE, 'heading': heading,
                            'active_episodes': ['Episode 1']}
                self.assertEqual('conflict', scraper.check_episode_identity(
                    identity, ENGLISH, expected_episode, [ROMAJI])['status'])

    def test_generic_episode_only_heading_can_use_a_matching_page_title(self):
        identity = {'url': EPISODE_PAGE, 'heading': 'Episode 1',
                    'og': ROMAJI + ' Episode 1', 'active_episodes': ['Episode 1']}
        self.assertEqual('consistent', scraper.check_episode_identity(
            identity, ENGLISH, '1', [ROMAJI])['status'])

    def test_romaji_catalog_query_prefers_its_known_english_title_within_one_lookup(self):
        sites = [{'name': 'first', 'url': 'https://provider.example',
                  'source': 'everythingmoe', 'score': 1}]
        page = SimpleNamespace(_ums_match_evidence=None)
        native = {'url': 'https://cdn.example/episode-1.m3u8', '_proxy_fetchable': True,
                  'audio_languages': ['eng'], 'selected_audio': 'dub',
                  'subtitles': [{'url': 'https://cdn.example/en.vtt', 'language': 'en'}]}
        with ExitStack() as stack:
            stack.enter_context(patch.object(scraper, 'fetch_indexed_sites', return_value=sites))
            stack.enter_context(patch.object(scraper, 'DRISSION_AVAILABLE', True))
            stack.enter_context(patch.object(scraper, 'launch_browser', return_value=page))
            close = stack.enter_context(patch.object(scraper, 'close_browser'))
            search = stack.enter_context(patch.object(scraper, 'search_site', return_value=[EPISODE_PAGE]))
            extract = stack.enter_context(patch.object(scraper, 'extract_from_mirror', return_value=native))
            stack.enter_context(patch.object(scraper, 'episode_url_candidates', return_value=[EPISODE_PAGE]))
            result = scraper.do_resolve(ROMAJI, '1', 1, 'dub', 35,
                title_aliases=[ENGLISH, ROMAJI], catalog_ids={'anilist': 154587, 'mal': 52991})
        self.assertIs(native, result)
        self.assertEqual(ENGLISH, search.call_args.args[1])
        self.assertEqual(ROMAJI, extract.call_args.kwargs['query'])
        self.assertEqual([ROMAJI, ENGLISH], extract.call_args.kwargs['title_aliases'])
        self.assertEqual({'anilist': 154587, 'mal': 52991}, extract.call_args.kwargs['catalog_ids'])
        self.assertEqual('dub', extract.call_args.kwargs['audio_preference'])
        self.assertEqual('en', result['subtitles'][0]['language'])
        close.assert_called_once()

    def test_conflicting_english_alias_cannot_replace_the_requested_season(self):
        query = 'Mushoku Tensei Season 3'
        wrong = 'Mushoku Tensei Season 2'
        self.assertEqual([query], scraper.safe_title_aliases(query, [wrong]))
        self.assertEqual(0, scraper.score_candidate_url(
            'https://provider.example/watch/mushoku-tensei-season-2/ep-1',
            wrong, query, '1', [wrong]))


if __name__ == '__main__':
    unittest.main()
