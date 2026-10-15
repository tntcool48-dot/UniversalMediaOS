"""Bounded catalog enrichment preserves whole work/unit identity without live requests."""
from contextlib import ExitStack
from copy import deepcopy
import importlib.util
from pathlib import Path
import time
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('anime_native_catalog_discovery',
    Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py')
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

QUERY = '葬送のフリーレン'
ENGLISH = 'Frieren: Beyond Journey’s End'
ROMAJI = 'Sousou no Frieren'
IDS = {'anilist': 154587, 'mal': 52991}
ALIASES = [ENGLISH, ROMAJI]
PAGE = 'https://provider.example/watch/AbCd1234/ep-1'
BASE = 'https://provider.example/search'


def media(native=QUERY, anime_id=154587, mal_id=52991, **changes):
    result = {'id': anime_id, 'idMal': mal_id, 'type': 'ANIME',
              'title': {'native': native, 'english': ENGLISH, 'romaji': ROMAJI}}
    result.update(changes)
    return result


def response(*records):
    return {'data': {'Page': {'pageInfo': {'hasNextPage': False},
                              'media': list(records)}}}


class AnimeNativeCatalogDiscoveryTests(unittest.TestCase):
    def test_unique_whole_native_title_supplies_only_its_primary_catalog_aliases(self):
        for mal_id in (52991, None):
            with self.subTest(mal_id=mal_id):
                payload = response(media(QUERY + ' 第2期', anime_id=182255),
                    media(mal_id=mal_id, synonyms=['Unrelated borrowed franchise']),
                    media(QUERY + ' ～●●の魔法～', anime_id=170068))
                with patch.object(scraper, '_fetch_native_catalog_metadata', return_value=payload) as fetch:
                    found = scraper.discover_native_catalog_identity(QUERY, deadline=time.monotonic() + 20)
                self.assertEqual({'title_aliases': ALIASES,
                    'catalog_ids': {'anilist': 154587, **({'mal': mal_id} if mal_id else {})}}, found)
                self.assertEqual(1, fetch.call_count)
                self.assertEqual(QUERY, fetch.call_args.args[0])
                timeout = fetch.call_args.kwargs.get('timeout')
                if timeout is None:
                    timeout = fetch.call_args.args[1]
                self.assertGreater(timeout, 0)
                self.assertLessEqual(timeout, 6)

    def test_prefix_sequel_and_related_ona_are_not_native_title_equivalence(self):
        for native in ('新' + QUERY, QUERY + ' 第2期', QUERY + ' ～●●の魔法～', 'フリーレン'):
            with self.subTest(native=native), patch.object(scraper, '_fetch_native_catalog_metadata',
                    return_value=response(media(native))):
                self.assertEqual({}, scraper.discover_native_catalog_identity(QUERY))

    def test_duplicate_exact_ids_or_records_do_not_establish_unique_identity(self):
        original = media()
        for other in (media(anime_id=170068), deepcopy(original),
                      media(title={'native': QUERY, 'english': 'Different Work', 'romaji': ROMAJI})):
            with self.subTest(other=other), patch.object(scraper, '_fetch_native_catalog_metadata',
                    return_value=response(original, other)):
                self.assertEqual({}, scraper.discover_native_catalog_identity(QUERY))

    def test_partial_paginated_malformed_or_invalid_catalog_data_fails_closed(self):
        valid = response(media())
        failures = [None, [], {}, {'data': None}, {'errors': [{'message': 'rate limited'}], **valid},
                    {'data': {'Page': None}}, response(), response(None), response('not a record')]
        for field, value in (('hasNextPage', True), ('hasNextPage', None), ('hasNextPage', 'false')):
            payload = deepcopy(valid)
            payload['data']['Page']['pageInfo'][field] = value
            failures.append(payload)
        payload = deepcopy(valid)
        del payload['data']['Page']['pageInfo']
        failures.append(payload)
        payload = deepcopy(valid)
        payload['data']['Page']['media'] = {'0': media()}
        failures.append(payload)
        for changes in ({'id': 0}, {'id': -1}, {'id': True}, {'id': '154587'},
                        {'idMal': True}, {'idMal': -1}, {'idMal': '52991'},
                        {'type': 'MANGA'}, {'type': None}, {'title': None},
                        {'title': {'english': ENGLISH, 'romaji': ROMAJI}}):
            failures.append(response(media(**changes)))
        for index, payload in enumerate(failures):
            with self.subTest(index=index), patch.object(scraper, '_fetch_native_catalog_metadata',
                    return_value=payload):
                self.assertEqual({}, scraper.discover_native_catalog_identity(QUERY))

    def test_metadata_timeout_preserves_unknown_identity(self):
        with patch.object(scraper, '_fetch_native_catalog_metadata', side_effect=TimeoutError('bounded timeout')):
            self.assertEqual({}, scraper.discover_native_catalog_identity(QUERY))

    def test_expired_deadline_does_not_start_metadata_work(self):
        with patch.object(scraper, '_fetch_native_catalog_metadata') as fetch:
            self.assertEqual({}, scraper.discover_native_catalog_identity(QUERY, deadline=time.monotonic() - 1))
        fetch.assert_not_called()

    def test_ascii_or_original_supplied_context_does_not_trigger_discovery(self):
        cases = [(ENGLISH, {}), (QUERY, {'title_aliases': [ROMAJI]}),
                 (QUERY, {'catalog_ids': IDS}), (QUERY, {'catalog_ids': {'mal': False}}),
                 (QUERY, {'catalog_ids': False}), (QUERY, {'catalog_ids': 0}),
                 (QUERY, {'catalog_ids': ''}), (QUERY, {'title_aliases': ['Unsearchable']})]
        site = {'name': 'automatic-index', 'url': 'https://provider.example',
                'source': 'everythingmoe', 'score': 1}
        for query, context in cases:
            with self.subTest(query=query, context=context), ExitStack() as stack:
                discover = stack.enter_context(patch.object(scraper, 'discover_native_catalog_identity'))
                stack.enter_context(patch.object(scraper, 'fetch_indexed_sites', return_value=[site]))
                stack.enter_context(patch.object(scraper, 'DRISSION_AVAILABLE', True))
                stack.enter_context(patch.object(scraper, 'launch_browser', return_value=Mock()))
                stack.enter_context(patch.object(scraper, 'close_browser'))
                stack.enter_context(patch.object(scraper, 'search_site', return_value=[PAGE]))
                stack.enter_context(patch.object(scraper, 'episode_url_candidates', return_value=[PAGE]))
                stack.enter_context(patch.object(scraper, 'extract_from_mirror',
                    return_value={'url': 'https://cdn.example/episode.m3u8', '_proxy_fetchable': True}))
                scraper.do_resolve(query, '1', 1, 'sub', 35, **context)
                discover.assert_not_called()

    def test_enriched_aliases_require_whole_own_card_primary_title_and_exact_unit(self):
        # Refeeding the accepted list must not spend the alias cap on its own query.
        accepted = scraper.safe_title_aliases(QUERY, ALIASES)
        self.assertEqual(accepted, scraper.safe_title_aliases(QUERY, accepted))
        for title in (ENGLISH, ROMAJI):
            with self.subTest(exact=title):
                html = f'<a href="{PAGE}">{title}</a>'
                self.assertEqual([PAGE], scraper.extract_candidate_links(html, BASE, QUERY, '1', accepted))
                browser = Mock()
                browser.run_js.return_value = [{'href': PAGE, 'text': title, 'label': ''}]
                self.assertEqual([PAGE], scraper.extract_browser_anchor_candidate_links(browser, BASE, QUERY, '1', accepted))
                identity = {'url': PAGE, 'heading': title + ' Episode 1', 'active_episodes': ['Episode 1']}
                found = scraper.check_episode_identity(identity, QUERY, '1', accepted, IDS)
                self.assertEqual('consistent', found['status'])
                self.assertTrue(found['alias_match'])
                self.assertFalse(found['catalog_id_match'])
                identity['primary_ids'] = {'anilist': 154587}
                self.assertTrue(scraper.check_episode_identity(identity, QUERY, '1', accepted, IDS)['catalog_id_match'])
        for title in (ROMAJI + ': ●● no Mahou', ENGLISH + ' Season 2', 'Beyond Journey’s End'):
            with self.subTest(related=title):
                html = f'<a href="{PAGE}">{title}</a><h2>{QUERY}</h2>'
                self.assertEqual([], scraper.extract_candidate_links(html, BASE, QUERY, '1', accepted))
                browser = Mock()
                browser.run_js.return_value = [{'href': PAGE, 'text': title, 'label': ''}]
                self.assertEqual([], scraper.extract_browser_anchor_candidate_links(browser, BASE, QUERY, '1', accepted))
                identity = {'url': PAGE, 'heading': title + ' Episode 1',
                            'title': ENGLISH + ' Episode 1', 'active_episodes': ['Episode 1']}
                self.assertEqual('conflict', scraper.check_episode_identity(identity, QUERY, '1', accepted, IDS)['status'])
        for changes in ({'active_episodes': ['Episode 10']}, {'active_episodes': ['Episode 1.5']},
                        {'primary_ids': {'mal': 16498}}):
            with self.subTest(conflict=changes):
                identity = {'url': PAGE, 'heading': ENGLISH + ' Episode 1', 'active_episodes': ['Episode 1']}
                identity.update(changes)
                self.assertEqual('conflict', scraper.check_episode_identity(identity, QUERY, '1', accepted, IDS)['status'])

    def test_alias_free_indexed_pipeline_uses_discovered_metadata_without_changing_unit_or_audio(self):
        site = {'name': 'automatic-index', 'url': 'https://provider.example',
                'source': 'everythingmoe', 'score': 1}
        for metadata, title, is_alias in ((response(media()), ENGLISH, True),
                                           (TimeoutError('bounded timeout'), QUERY, False)):
            with self.subTest(alias=is_alias), ExitStack() as stack:
                page = Mock(html=f'<h1>{title}</h1>', url=PAGE)
                page.get.return_value = True
                page.run_js.return_value = {'url': PAGE, 'heading': title,
                    'title': title + ' Episode 1', 'active_episodes': ['Episode 1']}
                native = {'url': 'https://cdn.example/episode.m3u8', '_proxy_fetchable': True,
                          'audio_languages': [], 'selected_audio': 'sub'}
                transport = stack.enter_context(patch.object(scraper, '_fetch_native_catalog_metadata'))
                if isinstance(metadata, Exception):
                    transport.side_effect = metadata
                else:
                    transport.return_value = metadata
                stack.enter_context(patch.object(scraper, 'fetch_indexed_sites', return_value=[site]))
                stack.enter_context(patch.object(scraper, 'DRISSION_AVAILABLE', True))
                stack.enter_context(patch.object(scraper, 'launch_browser', return_value=page))
                close = stack.enter_context(patch.object(scraper, 'close_browser'))
                fetch = stack.enter_context(patch.object(scraper, 'fetch_text',
                    return_value=(200, BASE, f'<a href="{PAGE}">{title}</a>')))
                stack.enter_context(patch.object(scraper, 'browser_search_site', return_value=[]))
                for name in ('setup_worker_hooks', 'set_navigation_referer', 'stop_media_listener'):
                    stack.enter_context(patch.object(scraper, name))
                stack.enter_context(patch.object(scraper, 'start_media_listener', return_value=False))
                stack.enter_context(patch.object(scraper, 'player_page_unavailable', return_value=False))
                stack.enter_context(patch.object(scraper, 'wait_for_dynamic_player', return_value={}))
                stack.enter_context(patch.object(scraper, 'stage_b_network_sniff', return_value=native))
                read_identity = stack.enter_context(patch.object(scraper, 'read_episode_identity', wraps=scraper.read_episode_identity))
                result = scraper.do_resolve(QUERY, '1', 1, 'sub', 35)
                self.assertIs(native, result)
                transport.assert_called_once()
                self.assertEqual(1, fetch.call_count)
                self.assertIn('keyword=', fetch.call_args.args[0])
                self.assertIn('%E8%91%AC', fetch.call_args.args[0])
                self.assertEqual((QUERY, '1'), read_identity.call_args.args[1:3])
                self.assertEqual('consistent', result['match_evidence']['status'])
                self.assertEqual(is_alias, result['match_evidence']['alias_match'])
                self.assertFalse(result['match_evidence']['catalog_id_match'])
                self.assertEqual([], result['audio_languages'])
                self.assertEqual('sub', result['selected_audio'])
                close.assert_called_once_with(page)


if __name__ == '__main__':
    unittest.main()
