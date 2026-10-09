"""Independent source-item matching and real caption handoff regressions."""
import json
import shutil
import subprocess
import time
import unittest
from unittest.mock import Mock, patch
from test_audiovisual_native_frames import av, ROOT, MEDIA


MOVIE = {'title': 'Dune 2021', 'imdb_id': 'tt1160419',
         'file_name': 'Dune (2021)/Dune.2021.1080p.mp4', 'stream_urls': [MEDIA]}
TV = {'title': 'Breaking Bad 2008', 'imdb_id': 'tt0903747',
      'season': '1', 'episode': '1', 'file_name': 'Seasons/Breaking.Bad.S01E01.Pilot.mkv', 'stream_urls': [MEDIA]}


class ItemEvidenceTests(unittest.TestCase):
    def scan_authenticated_item(self, token, api_suffix=''):
        api = 'https://data.example/api.php?type=tv&imdb=tt0903747'
        current = api + api_suffix + '&season=1&episode=1&stream_urls'
        from urllib.parse import quote
        requested = current if api_suffix or not token else current + '&api_token=' + quote(token, safe="~()*!.'-")
        item = {'apiUrl': requested, 'data': TV, 'default_subs': []}
        state = {'season': 1, 'episode': 1, 'allStreams': [MEDIA],
                 'currentCues': [{'start': 1, 'end': 2, 'text': 'English cue'}]}
        config = {'streamBase': api+api_suffix, 'apiToken': token, 'mediaId': 'tt0903747', 'mediaType': 'tv'}
        setup = {'__JW': {'CONFIG': config, 'state': state, 'SUB': {'fileName': TV['file_name'], 'activeKey': 'en'}},
                 '__umosAvItems': [item]}
        node = shutil.which('node')
        self.assertIsNotNone(node)
        program = 'global.window=' + json.dumps(setup) + ";window.vsFetchJSON=()=>{};" + \
            "global.localStorage={getItem:()=>JSON.stringify({vttUrl:'/en.vtt',lang:'en',label:'English'})};" + \
            'console.log(JSON.stringify(new Function(' + json.dumps(av.PLAYER_ITEM_SCAN) + ')()));'
        output = subprocess.run([node, '-e', program], capture_output=True, text=True, timeout=10, check=True)
        return json.loads(output.stdout), requested

    def test_current_provider_api_token_binds_the_original_item_without_refetch(self):
        snapshot, expected_api = self.scan_authenticated_item('current+access/token=')
        self.assertEqual(expected_api, snapshot['apiUrl'])
        page = Mock(url=ROOT, user_agent='agent')
        page.cookies.return_value = {}
        page.run_js.return_value = snapshot
        with patch.object(av, 'collect_subtitle_tracks', return_value=[]):
            result = av.enrich_native_result(av.stream_result(MEDIA), page, ROOT, time.monotonic()+5)
        self.assertEqual('tt0903747', result['evidence']['Identity']['ImdbId'])
        self.assertEqual(1, result['evidence']['Unit']['EpisodeNumber'])
        self.assertIn('English cue', result['subtitles'][0]['inline_vtt'])
        self.assertNotIn('Audio', result['evidence'])
        self.assertEqual(1, page.run_js.call_count)

    def test_scan_retains_unauthenticated_and_already_authenticated_api_addresses(self):
        for token, suffix in [('', ''), ('new-token', '&api_token=existing-token')]:
            with self.subTest(token=token, suffix=suffix):
                snapshot, expected = self.scan_authenticated_item(token, suffix)
                self.assertEqual(expected, snapshot['apiUrl'])

    def test_original_decoded_item_supplies_evidence_and_loaded_captions_without_refetch(self):
        saved = {'apiUrl': 'https://data.example/api.php?stream_urls', 'data': TV, 'default_subs': []}
        cues = [{'start': 1, 'end': 2, 'text': 'Loaded English'}]
        result, page = self.enrich(snapshot_changes={'items': [saved], 'activeCaption':
            {'url': '/en.vtt', 'language': 'en', 'default': True, 'cues': cues}})
        self.assertEqual('tt0903747', result['evidence']['Identity']['ImdbId'])
        self.assertEqual(1, result['evidence']['Unit']['EpisodeNumber'])
        self.assertIn('Loaded English', result['subtitles'][0]['inline_vtt'])
        self.assertEqual(1, page.run_js.call_count)
        self.assertNotIn('Audio', result['evidence'])

    def test_saved_other_api_release_or_stream_cannot_certify_current_media(self):
        saved = {'apiUrl': 'https://data.example/api.php?stream_urls', 'data': TV, 'default_subs': []}
        snapshots = {'apiUrl': saved['apiUrl'], 'streams': [MEDIA], 'captionFile': TV['file_name'], 'activeCaption': None}
        invalid = [dict(saved, apiUrl='https://other.example/api.php'),
                   dict(saved, data=dict(TV, file_name='Different.release.mkv')),
                   dict(saved, data=dict(TV, stream_urls=[MEDIA+'?episode=2'])),
                   dict(saved, data=dict(TV, stream_urls=['https://other.example/master.m3u8']))]
        for item in invalid:
            with self.subTest(item=item):
                page = Mock(url=ROOT, user_agent='agent')
                page.cookies.return_value = {}
                page.run_js.side_effect = [dict(snapshots, items=[item]), None]
                with patch.object(av, 'collect_subtitle_tracks', return_value=[]):
                    result = av.enrich_native_result(av.stream_result(MEDIA), page, ROOT, time.monotonic()+5)
                self.assertNotIn('evidence', result)
                self.assertNotIn('subtitles', result)

    def test_original_decoded_conflicting_movie_or_episode_still_rejects_native(self):
        for data, request in [(dict(MOVIE, imdb_id='tt0087182'), 'https://wrapper.example/embed/movie/tt1160419'),
                              (dict(TV, season='2', file_name='Breaking.Bad.S02E01.mkv'), ROOT)]:
            with self.subTest(data=data):
                api = 'https://data.example/api.php'
                page = Mock(url=request, user_agent='agent')
                page.cookies.return_value = {}
                page.run_js.return_value = {'apiUrl': api, 'streams': [MEDIA], 'captionFile': data['file_name'],
                    'items': [{'apiUrl': api, 'data': data}]}
                result = av.enrich_native_result(av.stream_result(MEDIA), page, request, time.monotonic()+5)
                self.assertIsNone(result)
                self.assertEqual(1, page.run_js.call_count)

    def test_provider_items_supply_title_year_id_and_exact_unit(self):
        movie, tv = av.provider_item_evidence(MOVIE), av.provider_item_evidence(TV)
        self.assertEqual('ProviderItem', movie['Origin'])
        self.assertEqual(('Dune', 2021, 'tt1160419'), tuple(movie['Identity'][key] for key in ['Title', 'Year', 'ImdbId']))
        self.assertEqual({}, movie['Unit'])
        self.assertEqual({'SeasonNumber': 1, 'EpisodeNumber': 1}, tv['Unit'])
        self.assertEqual('Series', tv['Identity']['ContentForm'])

    def test_filename_conflicts_do_not_become_independent_evidence(self):
        for data in [dict(TV, file_name='Breaking.Bad.S01E02.mkv'), dict(MOVIE, file_name='Dune.1984.mp4')]:
            with self.subTest(data=data), self.assertRaises(ValueError):
                av.provider_item_evidence(data)
        self.assertIsNone(av.provider_item_evidence({'title': 'Dune', 'imdb_id': 'tt1160419'}))

    def test_numeric_movie_title_and_underscore_episode_names_are_supported(self):
        data = dict(MOVIE, title='2001: A Space Odyssey 1968', file_name='2001.A.Space.Odyssey.1968.mp4')
        self.assertEqual(1968, av.provider_item_evidence(data)['Identity']['Year'])
        self.assertEqual(1, av.provider_item_evidence(dict(TV, file_name='Breaking_Bad_S01_E01_1080.mkv'))['Unit']['EpisodeNumber'])

    def test_only_added_access_token_can_differ_when_binding_a_stream(self):
        self.assertTrue(av.stream_belongs_to_item(MEDIA+'?token=access', MOVIE))
        self.assertFalse(av.stream_belongs_to_item(MEDIA+'?episode=2', MOVIE))
        self.assertFalse(av.stream_belongs_to_item('https://other.example/master.m3u8', MOVIE))

    def enrich(self, data=TV, snapshot_changes=None, caption_check=None):
        snapshot = {'apiUrl': 'https://data.example/api.php?stream_urls', 'streams': [MEDIA],
                    'captionFile': data['file_name'], 'activeCaption':
                    {'url': '/captions/episode.vtt', 'label': 'release.srt', 'language': 'English', 'default': True}}
        snapshot.update(snapshot_changes or {})
        page = Mock(url='https://player.example/embed/player/tv/tt0903747/1/1', user_agent='agent')
        page.cookies.return_value = {}
        page.run_js.side_effect = [snapshot, {'data': data, 'default_subs': []}]
        with patch.object(av, 'collect_subtitle_tracks', return_value=[]), \
             patch.object(av, 'caption_has_cues', caption_check or Mock(return_value=True)):
            result = av.enrich_native_result(av.stream_result(MEDIA), page, ROOT, time.monotonic()+10)
        return result, page

    def test_loaded_episode_cues_do_not_refetch_a_rate_limited_caption_origin(self):
        check = Mock(side_effect=AssertionError('Already loaded cues must not be fetched again'))
        active = {'url': '/en.vtt', 'label': 'English', 'language': 'en', 'default': True,
                  'cues': [{'start': 63.125, 'end': 65.75, 'text': 'Hello & مرحبا'}]}
        result, _ = self.enrich(snapshot_changes={'activeCaption': active}, caption_check=check)
        track = result['subtitles'][0]
        self.assertIn('00:01:03.125 --> 00:01:05.750', track['inline_vtt'])
        self.assertIn('Hello &amp; مرحبا', track['inline_vtt'])
        self.assertEqual(['en'], result['evidence']['Subtitles']['Languages'])
        check.assert_not_called()

    def test_loaded_cues_from_an_unbound_release_are_not_handed_off(self):
        active = {'url': '/en.vtt', 'language': 'en',
                  'cues': [{'start': 1, 'end': 2, 'text': 'Another episode'}]}
        result, _ = self.enrich(snapshot_changes={'captionFile': 'Other.release.mkv', 'activeCaption': active})
        self.assertNotIn('subtitles', result)

    def test_loaded_cues_require_valid_times_text_and_bounded_size(self):
        valid = {'start': 1, 'end': 2, 'text': 'A cue'}
        for cues in [[], [dict(valid, start=-1)], [dict(valid, end=1)], [dict(valid, start=float('nan'))],
                     [dict(valid, end=float('inf'))], [dict(valid, text='')], [dict(valid, start=True)],
                     [dict(valid, start=1.0001, end=1.0002)], [dict(valid, text='界'*1048576)], [valid]*20001]:
            with self.subTest(cues=str(cues)[:100]):
                self.assertEqual('', av.loaded_caption_vtt(cues))
        self.assertIn('WEBVTT\n', av.loaded_caption_vtt([valid]))

    def test_bound_item_and_timed_caption_are_handed_off_without_invented_audio(self):
        result, _ = self.enrich()
        self.assertEqual(1, result['evidence']['Unit']['EpisodeNumber'])
        self.assertNotIn('Audio', result['evidence'])
        caption = result['subtitles'][0]
        self.assertEqual('en', caption['language'])
        self.assertEqual('English', caption['label'])
        self.assertEqual('https://player.example/captions/episode.vtt', caption['url'])
        self.assertEqual(['en'], result['evidence']['Subtitles']['Languages'])

    def test_echoed_configuration_or_other_release_never_certifies_the_stream(self):
        result, _ = self.enrich(snapshot_changes={'apiUrl': '', 'CONFIG': TV})
        self.assertNotIn('evidence', result)
        result, _ = self.enrich(snapshot_changes={'captionFile': 'Another.release.mkv'})
        self.assertNotIn('evidence', result)
        self.assertNotIn('subtitles', result)

    def test_conflicting_server_id_or_episode_rejects_native_candidate(self):
        for data in [dict(TV, imdb_id='tt0944947'), dict(TV, episode='2')]:
            with self.subTest(data=data):
                result, _ = self.enrich(data)
                self.assertIsNone(result)

    def test_delayed_captions_wait_only_for_the_bound_active_item(self):
        initial = {'apiUrl': 'https://data.example/api.php', 'streams': [MEDIA],
                   'captionFile': TV['file_name'], 'activeCaption': None}
        for changed, expected in [(False, True), (True, False)]:
            refreshed = {**initial, 'captionFile': 'Other.release.mkv' if changed else TV['file_name'],
                'activeCaption': {'url': '/en.vtt', 'language': 'en', 'default': True}}
            page = Mock(url='https://player.example/embed/player/tv/tt0903747/1/1', user_agent='agent')
            page.cookies.return_value = {}
            page.run_js.side_effect = [initial, {'data': TV, 'default_subs': []}, None, refreshed]
            with patch.object(av, 'collect_subtitle_tracks', return_value=[]), patch.object(av, 'caption_has_cues', return_value=True):
                result = av.enrich_native_result(av.stream_result(MEDIA), page, ROOT, time.monotonic()+5)
            self.assertEqual(expected, bool(result.get('subtitles')))

    def test_caption_pages_without_timestamps_are_not_available_tracks(self):
        for payload, expected in [(b'WEBVTT\n\n00:01.000 --> 00:03.000\nDialogue', True),
                                  (b'<html>Blocked</html>', False), (b'WEBVTT\n\n', False)]:
            response = Mock(status_code=200)
            response.iter_content.return_value = [payload]
            with patch.object(av.cffi_req, 'get', return_value=response):
                self.assertEqual(expected, av.caption_has_cues({'url': 'https://captions.example/en.vtt'}, time.monotonic()+5))
            response.close.assert_called_once()

    def test_child_conflict_cannot_leave_ancestor_native_fallback_usable(self):
        root, child = Mock(url=ROOT), Mock(url='https://player.example/embed/tv/tt0903747/1/1')
        child.run_js.return_value = [1280, 720]
        root.get_frames.return_value = [child]
        child.get_frames.return_value = []
        with patch.object(av, 'start_listener'), patch.object(av, 'browser_dom_scan', return_value=(av.stream_result(MEDIA), [])), \
             patch.object(av, 'validated_stream', side_effect=lambda result, deadline: result), \
             patch.object(av, 'enrich_native_result', side_effect=lambda result, page, *_: result if page is root else None), \
             patch.object(av, 'click_player_controls', return_value=None):
            self.assertIsNone(av.scan_embedded_players(root, ROOT, time.monotonic()+10))


class PlayerItemCaptureTests(unittest.TestCase):
    def test_cross_site_player_observer_is_installed_before_resuming_target(self):
        page = Mock()
        av.install_item_capture(page)
        driver = page.browser._driver
        attached = driver.set_callback.call_args.args[1]
        driver.run.reset_mock()
        attached('frame-session', {'type': 'iframe'})
        calls = driver.run.call_args_list
        self.assertEqual(['Page.enable', 'Page.addScriptToEvaluateOnNewDocument', 'Target.setAutoAttach', 'Runtime.runIfWaitingForDebugger'],
                         [call.args[0] for call in calls])
        self.assertEqual(av.PLAYER_ITEM_CAPTURE, calls[1].kwargs['source'])
        self.assertTrue(calls[2].kwargs['waitForDebuggerOnStart'])
        self.assertTrue(all(call.kwargs['sessionId'] == 'frame-session' for call in calls))

    def test_observer_install_failure_still_resumes_owned_target(self):
        page = Mock()
        av.install_item_capture(page)
        driver = page.browser._driver
        attached = driver.set_callback.call_args.args[1]
        driver.run.reset_mock()
        driver.run.side_effect = [RuntimeError('observer unavailable'), {}]
        attached('frame-session', {'type': 'iframe'})
        self.assertEqual('Runtime.runIfWaitingForDebugger', driver.run.call_args.args[0])

    def run_capture(self, code):
        node = shutil.which('node')
        self.assertIsNotNone(node, 'Node is required to execute the provider response observer regressions')
        script = "const assert = require('node:assert/strict'); global.window = {};\n" + av.PLAYER_ITEM_CAPTURE + \
            "\n(async () => {\n" + code + "\n})().catch(error => { console.error(error); process.exitCode = 1; });"
        result = subprocess.run([node, '-'], input=script, text=True, capture_output=True, timeout=5)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_response_observer_preserves_promise_context_and_immutable_original_data(self):
        self.run_capture("const value = " + json.dumps({'status_code': '200', 'data': MOVIE}) + ";" + """
            const original = Promise.resolve(value);
            const owner = {name: 'owner'};
            let calls = 0;
            window.vsFetchJSON = function(url, opts) {
                calls++; assert.equal(this, owner); assert.equal(opts.flag, true); return original;
            };
            const result = window.vsFetchJSON.call(owner, 'https://data.example/api.php', {flag:true});
            assert.equal(result, original);
            assert.equal(await result, value);
            assert.equal(calls, 1);
            assert.equal(window.__umosAvItems.length, 1);
            value.data.imdb_id = 'tt0087182'; value.data.stream_urls[0] = 'https://other.example/other.m3u8';
            assert.equal(window.__umosAvItems[0].data.imdb_id, 'tt1160419');
            assert.equal(window.__umosAvItems[0].data.stream_urls[0], 'https://cdn.example/master.m3u8');
        """)

    def test_provider_rejection_and_synchronous_error_are_unchanged(self):
        self.run_capture("""
            const error = new Error('provider offline');
            const original = Promise.reject(error);
            window.vsFetchJSON = () => original;
            const result = window.vsFetchJSON('https://data.example/api.php');
            assert.equal(result, original);
            await assert.rejects(result, e => e === error);
            window.vsFetchJSON = () => { throw error; };
            assert.throws(() => window.vsFetchJSON('https://data.example/api.php'), e => e === error);
            assert.equal(window.__umosAvItems.length, 0);
        """)

    def test_echoed_config_failed_empty_or_excessive_responses_are_not_observed_items(self):
        self.run_capture("const data = " + json.dumps(MOVIE) + ";" + """
            window.CONFIG = data;
            assert.equal(window.__umosAvItems.length, 0);
            for (const value of [{status_code:403,data}, {status:403,data}, {data:{...data,stream_urls:[]}},
                    {data:{...data,stream_urls:Array(25).fill('https://cdn.example/a.m3u8')}},
                    {data:{...data,file_name:'x'.repeat(300000)}}]) {
                window.vsFetchJSON = () => Promise.resolve(value);
                assert.equal(await window.vsFetchJSON('https://data.example/api.php'), value);
            }
            assert.equal(window.__umosAvItems.length, 0);
        """)

    def test_response_retention_is_bounded_and_repeated_api_replaces_old_snapshot(self):
        self.run_capture("const data = " + json.dumps(MOVIE) + ";" + """
            let episode = 1;
            window.vsFetchJSON = () => Promise.resolve({data:{...data,episode},default_subs:Array(20).fill({language:'en'})});
            for (let i=0;i<6;i++) await window.vsFetchJSON('https://data.example/api.php?item='+i);
            assert.equal(window.__umosAvItems.length, 4);
            assert.equal(window.__umosAvItems[0].apiUrl, 'https://data.example/api.php?item=2');
            assert.equal(window.__umosAvItems[0].default_subs.length, 12);
            episode = 2;
            await window.vsFetchJSON('https://data.example/api.php?item=2');
            assert.equal(window.__umosAvItems.length, 4);
            assert.equal(window.__umosAvItems.at(-1).data.episode, 2);
        """)


if __name__ == '__main__':
    unittest.main()
