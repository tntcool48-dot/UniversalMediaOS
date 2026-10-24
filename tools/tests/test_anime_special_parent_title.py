"""A special's subtitle names the work; a page naming only its parent cannot stand in for it."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('anime_special_parent_scraper',
    Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py')
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)

# Live October 23 request: Re:Anime offered the parent TV series for this SPECIAL.
STAIRWAY = 'Kaguya-sama: Love Is War -Stairway to Adulthood-'
PARENT_URL = 'https://reanime.to/anime/kaguya-sama-love-is-war-5d97k2/ep-1'
NO_REGRETS = 'Attack on Titan: No Regrets'


def identity(heading, url, title='', episode='Episode 1'):
    return {'heading': heading, 'og': '', 'title': title, 'url': url, 'active_episodes': [episode]}


class SpecialParentTitleTests(unittest.TestCase):
    def test_parent_series_card_is_not_a_candidate_for_its_special(self):
        self.assertEqual(0, scraper.score_candidate_url(
            'https://reanime.to/anime/kaguya-sama-love-is-war-5d97k2', 'Kaguya-sama: Love is War', STAIRWAY, '1'))
        self.assertEqual(0, scraper.score_candidate_url(
            'https://provider.example/watch/attack-on-titan-112', 'Attack on Titan', NO_REGRETS, '1'))

    def test_parent_series_page_conflicts_with_the_requested_special(self):
        for query, heading, url in (
                (STAIRWAY, 'Kaguya-sama: Love is War', PARENT_URL),
                (NO_REGRETS, 'Attack on Titan', 'https://provider.example/watch/attack-on-titan-112/ep-1')):
            with self.subTest(query=query):
                evidence = scraper.check_episode_identity(identity(heading, url, 'Watch ' + heading), query, '1')
                self.assertEqual('conflict', evidence['status'])

    def test_matching_provider_catalog_id_still_identifies_a_shortened_heading(self):
        page = identity('Kaguya-sama: Love is War', 'https://provider.example/watch/9lwzq/ep-1')
        page['primary_ids'] = {'anilist': '194884'}
        evidence = scraper.check_episode_identity(page, STAIRWAY, '1', catalog_ids={'anilist': 194884})
        self.assertEqual('consistent', evidence['status'])
        page['primary_ids'] = {'anilist': '101921'}  # The parent series' own entry.
        evidence = scraper.check_episode_identity(page, STAIRWAY, '1', catalog_ids={'anilist': 194884})
        self.assertEqual('conflict', evidence['status'])

    def test_whole_special_title_remains_a_candidate_and_consistent(self):
        url = 'https://anikototv.to/watch/kaguya-sama-love-is-war-stairway-to-adulthood-9lwzq/ep-1'
        self.assertGreater(scraper.score_candidate_url(url, STAIRWAY, STAIRWAY, '1'), 0)
        evidence = scraper.check_episode_identity(identity(STAIRWAY, url), STAIRWAY, '1')
        self.assertEqual('consistent', evidence['status'])
        evidence = scraper.check_episode_identity(
            identity('Attack on Titan: No Regrets', 'https://provider.example/watch/aot-no-regrets/ep-1'), NO_REGRETS, '1')
        self.assertEqual('consistent', evidence['status'])

    def test_shortened_subtitle_and_numbered_sequel_wording_still_match(self):
        sequel = 'Mushoku Tensei: Jobless Reincarnation Season 2'
        evidence = scraper.check_episode_identity(
            identity('Mushoku Tensei Season 2', 'https://provider.example/watch/mushoku-tensei-season-2/ep-1'), sequel, '1')
        self.assertNotEqual('conflict', evidence['status'])
        arc = 'Demon Slayer: Kimetsu no Yaiba Swordsmith Village Arc'
        self.assertGreater(scraper.score_candidate_url(
            'https://provider.example/watch/demon-slayer-swordsmith-village-arc', 'Demon Slayer: Swordsmith Village Arc', arc, '1'), 0)

    def test_titles_without_a_subtitle_are_unchanged(self):
        evidence = scraper.check_episode_identity(
            identity('Hellsing Ultimate', 'https://provider.example/watch/hellsing-ultimate/ep-1'), 'Hellsing Ultimate', '1')
        self.assertEqual('consistent', evidence['status'])
        self.assertGreater(scraper.score_candidate_url(
            'https://provider.example/watch/re-zero', 'Re:ZERO -Starting Life in Another World-',
            'Re:ZERO -Starting Life in Another World-', '1'), 0)


if __name__ == '__main__':
    unittest.main()
