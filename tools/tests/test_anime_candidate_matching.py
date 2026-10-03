"""Regressions for the wrong-title browser fallback observed in the live app."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('anime_matching_scraper',
    Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py')
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

QUERY = 'Mushoku Tensei: Jobless Reincarnation Season 3'
BASE = 'https://index-provider.example/search?query=mushoku'


class AnchorPage:
    def __init__(self, anchors):
        self.anchors = anchors

    def run_js(self, script):
        return self.anchors


def anchor(path, text='', label=''):
    return {'href': 'https://index-provider.example' + path, 'text': text, 'label': label, 'cls': ''}


class AnimeCandidateMatchingTests(unittest.TestCase):
    def test_unrelated_watch_recommendation_cannot_become_a_match_from_path_bonus(self):
        wrong = anchor('/watch/16498/attack-on-titan', 'Attack on Titan')
        self.assertEqual(0, scraper.score_candidate_url(wrong['href'], wrong['text'], QUERY, '1'))
        self.assertEqual([], scraper.extract_browser_anchor_candidate_links(AnchorPage([wrong]), BASE, QUERY, '1'))

    def test_stale_card_label_does_not_override_a_conflicting_title_slug(self):
        stale = anchor('/watch/16498/attack-on-titan', QUERY)
        self.assertEqual([], scraper.extract_browser_anchor_candidate_links(AnchorPage([stale]), BASE, QUERY, '1'))

    def test_empty_watch_card_is_not_accepted_for_missing_title_evidence(self):
        empty = anchor('/watch/16498')
        self.assertEqual([], scraper.extract_browser_anchor_candidate_links(AnchorPage([empty]), BASE, QUERY, '1'))

    def test_one_shared_word_does_not_pass_the_minimum_title_match(self):
        wrong = anchor('/watch/123/my-home-hero', 'My Home Hero')
        self.assertEqual([], scraper.extract_browser_anchor_candidate_links(AnchorPage([wrong]), BASE, 'My Hero Academia', '1'))

    def test_matching_title_remains_available_among_unrelated_recommendations(self):
        wrong = anchor('/watch/16498/attack-on-titan', 'Attack on Titan')
        right = anchor('/watch/999/mushoku-tensei-jobless-reincarnation-season-3', QUERY)
        result = scraper.extract_browser_anchor_candidate_links(AnchorPage([wrong, right]), BASE, QUERY, '1')
        self.assertEqual([right['href']], result)

    def test_opaque_ids_remain_supported_when_the_card_supplies_the_title(self):
        right = anchor('/watch/AbCdEf12', label=QUERY)
        self.assertEqual([right['href']], scraper.extract_browser_anchor_candidate_links(AnchorPage([right]), BASE, QUERY, '1'))

    def test_wrong_season_is_rejected_even_when_the_card_label_claims_the_requested_one(self):
        url = 'https://provider.example/watch/mushoku-tensei-jobless-reincarnation-season-2'
        self.assertEqual(0, scraper.score_candidate_url(url, QUERY, QUERY, '1'))

    def test_wrong_part_and_special_cannot_survive_a_high_title_score(self):
        for suffix in ('season-3-part-2', 'season-3-cour-2', 'season-3-movie', 'season-3-special', 'season-3-ova'):
            with self.subTest(suffix=suffix):
                self.assertEqual(0, scraper.score_candidate_url(
                    'https://provider.example/watch/mushoku-tensei-jobless-reincarnation-' + suffix,
                    QUERY, QUERY, '1'))

    def test_explicit_numbered_variants_support_ordinal_roman_and_compact_forms(self):
        for text in ('Season 3', '3rd Season', 'Season III', 'S03E01'):
            with self.subTest(text=text):
                self.assertFalse(scraper.variant_conflicts('Mushoku Tensei ' + text, QUERY))
                self.assertTrue(scraper.variant_conflicts('Mushoku Tensei ' + text, 'Mushoku Tensei Season 2'))
        self.assertFalse(scraper.variant_conflicts('Mushoku Tensei 2nd Cour', 'Mushoku Tensei Part 2'))
        self.assertFalse(scraper.variant_conflicts('Mushoku Tensei Movie', 'Mushoku Tensei Movie'))

    def test_titles_without_a_numbered_variant_do_not_select_a_later_season(self):
        self.assertTrue(scraper.variant_conflicts('My Hero Academia Season 2', 'My Hero Academia'))
        self.assertFalse(scraper.variant_conflicts('My Hero Academia Season 1', 'My Hero Academia'))

    def test_unknown_variant_on_an_opaque_card_remains_a_discovery_candidate(self):
        right = anchor('/watch/AbCdEf12', label='Mushoku Tensei Jobless Reincarnation')
        self.assertEqual([right['href']], scraper.extract_browser_anchor_candidate_links(AnchorPage([right]), BASE, QUERY, '1'))

    def test_episode_bonus_does_not_match_episode_ten_or_opaque_numeric_ids(self):
        base = 'https://provider.example/watch/mushoku-tensei-jobless-reincarnation-season-3'
        score = scraper.score_candidate_url(base, QUERY, QUERY, '1')
        self.assertEqual(score, scraper.score_candidate_url(base + '?ep=10', QUERY, QUERY, '1'))
        self.assertEqual(score + 8, scraper.score_candidate_url(base + '?ep=1', QUERY, QUERY, '1'))
        self.assertEqual(set(), scraper.episode_claims('https://provider.example/watch/86/2022', is_url=True))

    def test_neighbor_card_cannot_supply_the_title_for_an_opaque_watch_link(self):
        html = '<a href="/watch/16498">Attack on Titan</a><h2>' + QUERY + '</h2>'
        self.assertEqual([], scraper.extract_candidate_links(html, BASE, QUERY, '1'))

    def test_card_image_alt_is_title_evidence_and_real_links_avoid_slug_guesses(self):
        href = '/watch/mushoku-tensei-jobless-reincarnation-season-3-abcd1234'
        html = '<a href="' + href + '"><img alt="' + QUERY + '"></a>'
        self.assertEqual(['https://index-provider.example' + href],
            scraper.extract_candidate_links(html, BASE, QUERY, '1'))

    def test_episode_rewrites_never_append_the_known_wrong_original(self):
        for suffix in ('/ep-1', '/episode-1', '-episode-1', '/episode/1', '?ep=1', '?episode=1'):
            with self.subTest(suffix=suffix):
                url = 'https://provider.example/watch/mushoku-tensei' + suffix
                candidates = scraper.episode_url_candidates(url, '2')
                self.assertEqual(1, len(candidates))
                self.assertNotIn(url, candidates)
                self.assertEqual({2}, scraper.episode_claims(candidates[0], is_url=True))

    def test_path_and_duplicate_query_units_rewrite_together_and_keep_unrelated_fields(self):
        url = 'https://provider.example/watch/mushoku/episode-10?ep=10&episode=10&lang=sub'
        self.assertEqual(['https://provider.example/watch/mushoku/episode-2?ep=2&episode=2&lang=sub'],
            scraper.episode_url_candidates(url, '2'))

    def test_numeric_titles_and_player_ids_are_not_rewritten_as_units(self):
        url = 'https://provider.example/watch/86/AbCd1234'
        self.assertIn(url + '/ep-2', scraper.episode_url_candidates(url, '2'))
        self.assertIn(url, scraper.episode_url_candidates(url, '2'))


class AnimePageIdentityTests(unittest.TestCase):
    def identity(self, **changes):
        result = {'url': 'https://provider.example/watch/mushoku-tensei-season-3/ep-1',
                  'heading': QUERY, 'title': QUERY + ' Episode 1 Watch Anime Online',
                  'active_episodes': ['1\nEpisode 1']}
        result.update(changes)
        return result

    def test_page_title_and_selected_episode_establish_independent_consistency(self):
        found = scraper.check_episode_identity(self.identity(), QUERY, '1')
        self.assertEqual('consistent', found['status'])
        self.assertEqual([3], found['observed_seasons'])
        self.assertEqual([1], found['observed_episodes'])

    def test_matching_active_episode_mal_id_is_independent_evidence(self):
        found = scraper.check_episode_identity(self.identity(
            active_id_claims=[{'mal': '52991'}]), QUERY, '1',
            catalog_ids={'mal': 52991, 'anilist': 154587})
        self.assertEqual('consistent', found['status'])
        self.assertTrue(found['catalog_id_match'])
        self.assertEqual({'mal': [52991]}, found['observed_catalog_ids'])

    def test_wrong_primary_or_active_id_rejects_before_media_extraction(self):
        for claims in ({'primary_ids': {'mal': '16498'}},
                       {'active_id_claims': [{'anilist': '16498'}]}):
            with self.subTest(claims=claims):
                found = scraper.check_episode_identity(self.identity(**claims), QUERY, '1',
                    catalog_ids={'mal': 52991, 'anilist': 154587})
                self.assertEqual('conflict', found['status'])
                self.assertTrue(found['catalog_id_conflict'])

    def test_matching_id_cannot_override_wrong_title_or_episode(self):
        for changes in ({'heading': 'Attack on Titan Episode 1'},
                        {'active_episodes': ['Episode 10']}):
            with self.subTest(changes=changes):
                found = scraper.check_episode_identity(self.identity(
                    primary_ids={'mal': '52991'}, **changes), QUERY, '1',
                    catalog_ids={'mal': 52991})
                self.assertEqual('conflict', found['status'])
                self.assertTrue(found['catalog_id_match'])

    def test_missing_id_preserves_existing_title_and_unit_behavior(self):
        found = scraper.check_episode_identity(self.identity(), QUERY, '1',
            catalog_ids={'mal': 52991})
        self.assertEqual('consistent', found['status'])
        self.assertFalse(found['catalog_id_match'])

    def test_only_primary_canonical_id_url_counts_not_recommendations(self):
        found = scraper.check_episode_identity(self.identity(
            primary_id_urls=['https://myanimelist.net/anime/52991/Fixture']), QUERY, '1',
            catalog_ids={'mal': 52991})
        self.assertTrue(found['catalog_id_match'])
        unrelated = scraper.check_episode_identity(self.identity(
            primary_id_urls=['https://other.example/anime/52991']), QUERY, '1',
            catalog_ids={'mal': 52991})
        self.assertFalse(unrelated['catalog_id_match'])

    def test_invalid_catalog_ids_do_not_become_evidence(self):
        self.assertEqual({'mal': 52991}, scraper.safe_catalog_ids(
            {'mal': '52991', 'anilist': True}))
        self.assertEqual({}, scraper.safe_catalog_ids({'mal': '-1', 'anilist': '0'}))

    def test_wrong_active_episode_rejects_even_when_the_requested_url_is_correct(self):
        found = scraper.check_episode_identity(self.identity(active_episodes=['Episode 10']), QUERY, '1')
        self.assertEqual('conflict', found['status'])

    def test_parent_redirect_to_another_season_rejects_a_stale_correct_heading(self):
        found = scraper.check_episode_identity(self.identity(
            url='https://provider.example/watch/mushoku-tensei-season-2/ep-1'), QUERY, '1')
        self.assertEqual('conflict', found['status'])

    def test_wrong_heading_rejects_a_stale_matching_document_title(self):
        found = scraper.check_episode_identity(self.identity(heading='Attack on Titan'), QUERY, '1')
        self.assertEqual('conflict', found['status'])

    def test_requested_route_without_source_unit_evidence_is_not_marked_consistent(self):
        found = scraper.check_episode_identity(self.identity(title=QUERY, active_episodes=[]), QUERY, '1')
        self.assertEqual('unknown', found['status'])
        self.assertEqual([], found['observed_episodes'])

    def test_source_without_season_evidence_does_not_verify_the_requested_season(self):
        found = scraper.check_episode_identity(self.identity(heading='Mushoku Tensei',
            title='Mushoku Tensei Episode 1'), QUERY, '1')
        self.assertEqual('unknown', found['status'])
        self.assertEqual([], found['observed_seasons'])

    def test_selected_server_number_does_not_become_episode_evidence(self):
        page = Mock(url='https://provider.example/watch/mushoku/ep-1')
        page.run_js.return_value = self.identity(title=QUERY, active_episodes=[])
        self.assertTrue(scraper.read_episode_identity(page, QUERY, '1'))
        self.assertEqual([], page._ums_match_evidence['observed_episodes'])
        self.assertIn('e.closest', page.run_js.call_args.args[0])
        self.assertIn('active_id_claims', page.run_js.call_args.args[0])

    def test_conflicting_provider_id_stops_before_stream_capture(self):
        page = Mock(html='<h1>' + QUERY + '</h1>')
        page.get.return_value = True
        page.run_js.return_value = self.identity(primary_ids={'mal': '16498'})
        with patch.object(scraper, 'player_page_unavailable', return_value=False), \
             patch.object(scraper, 'wait_for_dynamic_player', return_value={}), \
             patch.object(scraper, 'listen_for_media') as listen, \
             patch.object(scraper, 'stage_b_network_sniff') as stage_b:
            result = scraper.extract_from_mirror('', BASE, page, query=QUERY,
                episode_id='1', catalog_ids={'mal': 52991})
        self.assertIsNone(result)
        listen.assert_not_called()
        stage_b.assert_not_called()
        self.assertTrue(page._ums_match_evidence['catalog_id_conflict'])


class AnimeNavigationTests(unittest.TestCase):
    def test_confirmed_404_parent_skips_dynamic_and_network_waits(self):
        page = Mock(html='<title>404 — Error | fixture</title><h1>404</h1>')
        with patch.object(scraper, 'wait_for_dynamic_player') as wait, \
             patch.object(scraper, 'listen_for_media') as listen:
            self.assertIsNone(scraper.extract_from_mirror('', BASE, page))
        wait.assert_not_called()
        listen.assert_not_called()

    def test_episode_404_title_is_not_an_unavailable_page(self):
        page = Mock(html='<title>My Anime Episode 404 Watch Anime Online</title>')
        self.assertFalse(scraper.player_page_unavailable(page))

    def test_expired_control_work_stops_before_scanning_or_clicking(self):
        page = Mock()
        self.assertEqual(0, scraper.activate_player_controls(page, deadline=scraper.time.monotonic() - 1))
        page.eles.assert_not_called()

    def test_control_scan_stops_when_budget_expires_before_the_next_selector(self):
        page = Mock()
        page.eles.return_value = []
        with patch.object(scraper, 'deadline_expired', side_effect=[False, True]):
            self.assertEqual(0, scraper.activate_player_controls(page, deadline=1))
        page.eles.assert_called_once()

    def test_budget_consumed_by_controls_does_not_start_another_network_wait(self):
        page = Mock()
        with patch.object(scraper, 'deadline_expired', side_effect=[False, True]), \
             patch.object(scraper, 'activate_player_controls', return_value=1) as controls, \
             patch.object(scraper, 'listen_for_media') as listen:
            self.assertIsNone(scraper.stage_b_network_sniff(page, deadline=123))
        self.assertEqual(123, controls.call_args.kwargs['deadline'])
        listen.assert_not_called()

    def test_failed_parent_visit_cannot_accept_a_previous_page_stream(self):
        page = Mock()
        page.get.return_value = False
        with patch.object(scraper, 'wait_for_dynamic_player') as wait, \
             patch.object(scraper, 'listen_for_media') as listen:
            self.assertIsNone(scraper.extract_from_mirror('', 'https://provider.example/watch/episode-2',
                page, query=QUERY, episode_id='2'))
        wait.assert_not_called()
        listen.assert_not_called()
        self.assertEqual(0, page.get.call_args.kwargs['retry'])

    def test_failed_iframe_visit_cannot_accept_a_previous_page_stream(self):
        page = Mock()
        page.get.return_value = False
        with patch.object(scraper, 'start_media_listener', return_value=True), \
             patch.object(scraper, 'listen_for_media') as listen:
            self.assertIsNone(scraper.extract_player_page(page, 'https://player.example/embed/2', BASE))
        listen.assert_not_called()
        self.assertEqual(0, page.get.call_args.kwargs['retry'])

    def test_failed_search_visit_cannot_reuse_previous_candidates(self):
        page = Mock()
        page.get.return_value = False
        with patch.object(scraper, 'build_search_urls', return_value=[BASE]), \
             patch.object(scraper, 'extract_candidate_links') as extract:
            self.assertEqual([], scraper.browser_search_site(page,
                {'name': 'fixture', 'source': 'index', 'url': BASE}, QUERY, '1'))
        extract.assert_not_called()
        self.assertEqual(0, page.get.call_args.kwargs['retry'])

    def test_conflicting_parent_unit_stops_before_any_media_extraction(self):
        page = Mock()
        with patch.object(scraper, 'wait_for_dynamic_player', return_value={}), \
             patch.object(scraper, 'read_episode_identity', return_value=False), \
             patch.object(scraper, 'listen_for_media') as listen, \
             patch.object(scraper, 'stage_b_network_sniff') as stage_b:
            self.assertIsNone(scraper.extract_from_mirror('', BASE, page, query=QUERY, episode_id='1'))
        listen.assert_not_called()
        stage_b.assert_not_called()

    def test_ready_server_only_parent_skips_empty_autoplay_wait(self):
        page = Mock()
        native = {'url': 'https://cdn.example/video.m3u8', '_proxy_fetchable': True}
        with patch.object(scraper, 'wait_for_dynamic_player',
                return_value={'server_controls': 4, 'iframes': 0, 'video': False}), \
             patch.object(scraper, 'read_episode_identity', return_value=True), \
             patch.object(scraper, 'listen_for_media') as listen, \
             patch.object(scraper, 'stage_b_network_sniff', return_value=native):
            self.assertIs(native, scraper.extract_from_mirror('', BASE, page, query=QUERY, episode_id='1'))
        listen.assert_not_called()

    def test_direct_video_parent_preserves_initial_capture_and_captions(self):
        page = Mock()
        native = {'url': 'https://cdn.example/video.m3u8', '_proxy_fetchable': True,
                  'subtitles': [{'url': 'https://cdn.example/en.vtt', 'language': 'en'}]}
        with patch.object(scraper, 'wait_for_dynamic_player',
                return_value={'server_controls': 4, 'iframes': 0, 'video': True}), \
             patch.object(scraper, 'read_episode_identity', return_value=True), \
             patch.object(scraper, 'listen_for_media', return_value=native) as listen, \
             patch.object(scraper, 'stage_b_network_sniff') as select:
            self.assertIs(native, scraper.extract_from_mirror('', BASE, page, query=QUERY, episode_id='1'))
        listen.assert_called_once()
        select.assert_not_called()

    def test_ready_controls_return_without_the_unconditional_one_second_sleep(self):
        page = Mock()
        page.eles.side_effect = [[], [Mock()]]
        page.ele.return_value = None
        with patch.object(scraper.time, 'sleep') as sleep:
            state = scraper.wait_for_dynamic_player(page, seconds=1)
        self.assertEqual(1, state['server_controls'])
        sleep.assert_not_called()

    def test_group_metadata_is_read_in_one_call_without_losing_dub_selection(self):
        page = Mock()
        sub, dub = Mock(), Mock()
        sub.run_js.return_value = {'group': 'sub', 'values': ['one', 'English subtitles']}
        dub.run_js.return_value = {'group': 'dub', 'values': ['two', 'Vidstream']}
        page.eles.return_value = [sub, dub]
        with patch.object(scraper, 'raw_iframe_sources', side_effect=[[], ['https://player.example/dub/2']]), \
             patch.object(scraper.time, 'sleep'):
            self.assertEqual(1, scraper.activate_player_controls(page, audio_preference='dub'))
        sub.click.assert_not_called()
        dub.click.assert_called_once()
        sub.attr.assert_not_called()
        dub.attr.assert_not_called()
        self.assertEqual({'https://player.example/dub/2': 'dub'}, page._ums_selected_audio_frames)


if __name__ == '__main__':
    unittest.main()
