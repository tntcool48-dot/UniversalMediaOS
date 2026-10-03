"""Regressions for missing captions and Japanese-only results accepted as Dub."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('caption_audio_scraper',
    Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py')
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)


def ts(language):
    audio = b'\x0f\xe1\x01\xf0\x06\x0a\x04' + language.encode('ascii') + b'\x00'
    body = b'\x00\x01\xc1\x00\x00\xe1\x00\xf0\x00' + audio + b'\x00' * 4
    section = b'\x02' + (0xb000 | len(body)).to_bytes(2, 'big') + body
    first = b'\x47\x50\x00\x10\x00' + section
    filler = b'\x47\x01\x00\x10' + b'\xff' * 184
    return first.ljust(188, b'\xff') + filler * 3


def response(url, data=None, text=None, status=200):
    result = Mock(status_code=status, url=url, headers={'content-type':
        'application/vnd.apple.mpegurl' if text is not None else 'video/mp2t'})
    result.text = text or ''
    result.iter_content.return_value = iter([data or b''])
    return result


class Page:
    url = 'https://player.example/episode/1'
    user_agent = 'Fixture agent'
    html = '<video></video>'

    def __init__(self, tracks=None):
        self.tracks = tracks or []
        self._ums_subtitle_tracks = []

    def cookies(self, **_):
        return {'session': 'fixture'}

    def run_js(self, script):
        return self.tracks if 'const tracks = []' in script else ['https://cdn.example/video.m3u8']


class CaptionsAudioTests(unittest.TestCase):
    def test_native_handoff_carries_the_variant_whose_bytes_were_validated(self):
        result = {'url': 'https://cdn.example/master.m3u8'}
        master = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5500000\n1080.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nbroken-480.m3u8\n'
        get = Mock(side_effect=[response(result['url'], text=master),
            response('https://cdn.example/1080.m3u8', text='#EXTM3U\n#EXTINF:4,\nfirst.ts\n'),
            response('https://cdn.example/first.ts', ts('jpn'))])
        with patch.object(scraper, 'cffi_req', Mock(get=get)), \
             patch.object(scraper, 'is_safe_public_url', return_value=True):
            self.assertTrue(scraper.is_proxy_fetchable_result(result))
        self.assertEqual('https://cdn.example/1080.m3u8', result['validated_hls_variant'])
        self.assertEqual('https://cdn.example/master.m3u8', result['url'])
        self.assertEqual(3, get.call_count)

    def test_failed_variant_never_publishes_native_selection_evidence(self):
        result = {'url': 'https://cdn.example/master.m3u8'}
        get = Mock(side_effect=[response(result['url'], text='#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nbroken.m3u8\n'),
            response('https://cdn.example/broken.m3u8', text='<html>not found</html>', status=404)])
        with patch.object(scraper, 'cffi_req', Mock(get=get)), \
             patch.object(scraper, 'is_safe_public_url', return_value=True):
            self.assertFalse(scraper.is_proxy_fetchable_result(result))
        self.assertNotIn('validated_hls_variant', result)

    def test_mislabeled_html_and_javascript_segments_use_transport_bytes(self):
        for content_type in ('image/jpeg', 'text/html', 'application/javascript', 'application/json'):
            with self.subTest(content_type=content_type):
                self.assertTrue(scraper.payload_looks_like_media(ts('eng'), content_type))
                self.assertFalse(scraper.payload_looks_like_media(b'<html>Blocked</html>' + b' ' * 1024, content_type))
                self.assertFalse(scraper.payload_looks_like_media(b'{"error":"blocked"}' + b' ' * 1024, content_type))

    def test_transport_metadata_distinguishes_english_and_japanese(self):
        for language in ('eng', 'jpn'):
            self.assertEqual([language], scraper.audio_languages_from_payload(ts(language)))
        self.assertEqual([], scraper.audio_languages_from_payload(ts('jpn')[:25]))

    def test_mp4_video_language_cannot_be_mistaken_for_audio_language(self):
        def box(kind, body):
            return (len(body) + 8).to_bytes(4, 'big') + kind + body
        def track(kind, language):
            packed = sum((ord(letter) - 96) << shift for letter, shift in zip(language, (10, 5, 0)))
            mdhd = box(b'mdhd', b'\0' * 20 + packed.to_bytes(2, 'big') + b'\0\0')
            hdlr = box(b'hdlr', b'\0' * 8 + kind + b'\0' * 12)
            return box(b'trak', box(b'mdia', mdhd + hdlr))
        data = box(b'moov', track(b'vide', 'eng') + track(b'soun', 'jpn'))
        self.assertEqual(['jpn'], scraper.audio_languages_from_payload(data))

    def test_every_stage_rejects_japanese_only_dub_without_a_site_downgrade(self):
        for stage in ('Initial network', 'Stage A', 'Stage B', 'Stage C', 'Stage D'):
            with self.subTest(stage=stage):
                result = {'url': 'https://cdn.example/video.mp4', 'referer': Page.url}
                media = response(result['url'], ts('jpn'))
                with patch.object(scraper, 'cffi_req', Mock(get=Mock(return_value=media))), \
                     patch.object(scraper, 'is_safe_public_url', return_value=True):
                    self.assertIsNone(scraper.accept_stream_result(result, stage, 'dub'))
                self.assertEqual(['jpn'], result['audio_languages'])
                media.close.assert_called_once()

    def test_english_dub_is_accepted_and_outer_stage_reuses_validation(self):
        result = {'url': 'https://cdn.example/video.mp4', 'referer': Page.url}
        get = Mock(return_value=response(result['url'], ts('eng')))
        with patch.object(scraper, 'cffi_req', Mock(get=get)), \
             patch.object(scraper, 'is_safe_public_url', return_value=True):
            self.assertIs(result, scraper.accept_stream_result(result, 'Iframe Stage D', 'dub'))
            self.assertIs(result, scraper.accept_stream_result(result, 'Stage C', 'dub'))
        get.assert_called_once()

    def test_unknown_audio_is_not_claimed_as_english_but_sub_remains_usable(self):
        unknown = {'url': 'https://cdn.example/video.mp4', '_proxy_fetchable': True}
        self.assertIsNone(scraper.accept_stream_result(unknown, 'Stage D', 'dub'))
        self.assertIs(unknown, scraper.accept_stream_result(unknown, 'Stage D', 'sub'))

    def test_selected_dub_frame_without_tags_remains_usable_without_claiming_english(self):
        for languages in ([], ['und'], [None]):
            result = {'url': 'https://cdn.example/video.mp4', '_proxy_fetchable': True,
                'audio_languages': languages, 'selected_audio': 'dub'}
            self.assertIs(result, scraper.accept_stream_result(result, 'Iframe network', 'dub'))
            self.assertEqual(languages, result['audio_languages'])

    def test_japanese_metadata_overrides_selected_dub_frame(self):
        result = {'url': 'https://cdn.example/video.mp4', '_proxy_fetchable': True,
            'audio_languages': ['jpn'], 'selected_audio': 'dub'}
        self.assertIsNone(scraper.accept_stream_result(result, 'Iframe network', 'dub'))

    def test_dub_selection_evidence_requires_a_changed_frame(self):
        for changed in (True, False):
            page = Mock()
            control = Mock(text='Vidstream')
            control.attr.return_value = ''
            control.run_js.return_value = 'dub'
            page.eles.return_value = [control]
            before = ['https://player.example/sub/1']
            after = ['https://player.example/dub/2'] if changed else before
            with patch.object(scraper, 'raw_iframe_sources', side_effect=[before, after]), \
                 patch.object(scraper.time, 'sleep'):
                scraper.activate_player_controls(page, audio_preference='dub')
            self.assertEqual({'https://player.example/dub/2': 'dub'} if changed else {},
                page._ums_selected_audio_frames)
            self.assertIsNone(page._ums_selected_audio)

    def test_only_the_selected_frame_receives_provider_audio_evidence(self):
        page = Page()
        page._ums_selected_audio_frames = {'https://player.example/dub/2': 'dub'}
        sources = ['https://player.example/unrelated', 'https://player.example/dub/2']
        with patch.object(scraper, 'collect_iframe_sources', return_value=sources), \
             patch.object(scraper, 'extract_player_page', side_effect=[None, {'url': 'fixture'}]) as extract:
            scraper.stage_c_iframe_crawl(page, audio_preference='dub')
        self.assertIsNone(extract.call_args_list[0].kwargs['selected_audio'])
        self.assertEqual('dub', extract.call_args_list[1].kwargs['selected_audio'])

    def test_explicit_sub_group_is_not_dub_because_it_mentions_english(self):
        page = Mock()
        sub, dub = Mock(text='English subtitles'), Mock(text='Vidstream')
        sub.attr.return_value = dub.attr.return_value = ''
        sub.run_js.return_value, dub.run_js.return_value = 'sub', 'dub'
        page.eles.return_value = [sub, dub]
        with patch.object(scraper, 'raw_iframe_sources', return_value=[]), \
             patch.object(scraper.time, 'sleep'):
            scraper.activate_player_controls(page, audio_preference='dub')
        sub.click.assert_not_called()
        dub.click.assert_called_once()

    def test_multiple_changed_frames_do_not_claim_the_selected_audio(self):
        page = Mock()
        control = Mock(text='Dub')
        control.attr.return_value = ''
        control.run_js.return_value = 'dub'
        page.eles.return_value = [control]
        with patch.object(scraper, 'raw_iframe_sources', side_effect=[[],
                ['https://player.example/one', 'https://player.example/two']]), \
             patch.object(scraper.time, 'sleep'):
            scraper.activate_player_controls(page, audio_preference='dub')
        self.assertEqual({}, page._ums_selected_audio_frames)

    def test_verified_english_hls_rendition_survives_japanese_default(self):
        master = '#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",LANGUAGE="en",URI="eng.m3u8"\n#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO="a"\nvideo.m3u8\n'
        result = {'url': 'https://cdn.example/master.m3u8'}
        replies = [response(result['url'], text=master),
            response('https://cdn.example/video.m3u8', text='#EXTM3U\n#EXTINF:4,\nvideo.ts\n'),
            response('https://cdn.example/video.ts', ts('jpn')),
            response('https://cdn.example/eng.m3u8', text='#EXTM3U\n#EXTINF:4,\neng.aac\n'),
            response('https://cdn.example/eng.aac', b'\xff\xf1' + b'\0' * 1024)]
        with patch.object(scraper, 'cffi_req', Mock(get=Mock(side_effect=replies))), \
             patch.object(scraper, 'is_safe_public_url', return_value=True):
            self.assertIs(result, scraper.accept_stream_result(result, 'Initial network', 'dub'))
        self.assertEqual(['jpn', 'eng'], result['audio_languages'])

    def test_iframe_crawl_continues_after_wrong_audio(self):
        def result(url, *_):
            return {'url': url, '_proxy_fetchable': True,
                'audio_languages': ['eng' if 'english' in url else 'jpn']}
        sources = ['https://cdn.example/japanese.mp4', 'https://cdn.example/english.mp4']
        with patch.object(scraper, 'collect_iframe_sources', return_value=sources), \
             patch.object(scraper, 'make_stream_result', side_effect=result):
            found = scraper.stage_c_iframe_crawl(Page(), audio_preference='dub')
        self.assertEqual(sources[1], found['url'])

    def test_cached_sub_stream_does_not_hide_a_later_english_stream(self):
        def result(url, *_):
            return {'url': url, '_proxy_fetchable': True,
                'audio_languages': ['eng' if 'english' in url else 'jpn']}
        sources = ['https://cdn.example/japanese.m3u8', 'https://cdn.example/english.m3u8']
        with patch.object(scraper, 'extract_dom_media_urls', return_value=sources), \
             patch.object(scraper, 'make_stream_result', side_effect=result):
            found = scraper.stage_d_carpet_bomb(Page(), 'dub')
        self.assertEqual(sources[1], found['url'])

    def test_parent_audio_group_selects_dub_without_clicking_back_to_sub(self):
        def server(audio):
            el = Mock(text='Vidstream')
            el.attr.return_value = ''
            el.run_js.return_value = audio
            return el
        sub, dub = server('sub'), server('dub')
        page = Mock()
        page.eles.return_value = [sub, dub]
        with patch.object(scraper.time, 'sleep'):
            self.assertEqual(1, scraper.activate_player_controls(page, audio_preference='dub'))
        sub.click.assert_not_called()
        dub.click.assert_called_once()

    def test_dead_dub_server_is_skipped_on_the_next_selection(self):
        first, second = Mock(text='Server one'), Mock(text='Server two')
        for el in (first, second):
            el.attr.return_value = ''
            el.run_js.return_value = 'dub'
        page = Mock()
        page.eles.return_value = [first, second]
        excluded = set()
        with patch.object(scraper.time, 'sleep'):
            scraper.activate_player_controls(page, audio_preference='dub', excluded=excluded)
            scraper.activate_player_controls(page, audio_preference='dub', excluded=excluded)
        first.click.assert_called_once()
        second.click.assert_called_once()

    def test_sub_controls_never_click_the_dub_group(self):
        sub, dub = Mock(text='Vidstream'), Mock(text='Vidstream')
        for el, audio in ((sub, 'sub'), (dub, 'dub')):
            el.attr.return_value = ''
            el.run_js.return_value = audio
        page = Mock()
        page.eles.return_value = [dub, sub]
        with patch.object(scraper.time, 'sleep'):
            self.assertEqual(1, scraper.activate_player_controls(page, audio_preference='sub'))
        sub.click.assert_called_once()
        dub.click.assert_not_called()

    def test_deleted_file_page_avoids_idle_network_wait(self):
        page = Mock(html='<div class="error-code">Error Code: <span>410</span></div>')
        with patch.object(scraper, 'start_media_listener', return_value=True), \
             patch.object(scraper, 'listen_for_media') as listen:
            self.assertIsNone(scraper.extract_player_page(page, 'https://player.example/embed/1',
                Page.url, audio_preference='dub'))
        listen.assert_not_called()

    def test_network_caption_headers_and_dom_language_are_merged(self):
        url = 'https://cdn.example/eng.vtt?token=fixture'
        page = Page([{'src': url, 'kind': 'subtitles', 'label': 'English', 'srclang': 'en'}])
        page._ums_subtitle_tracks = [{'url': url, 'label': 'Captions', 'language': '',
            'headers': {'X-Caption-Token': 'fixture'}}]
        with patch.object(scraper, 'is_safe_public_url', return_value=True):
            tracks = scraper.collect_subtitle_tracks(page)
        self.assertEqual(1, len(tracks))
        self.assertEqual('English', tracks[0]['label'])
        self.assertEqual('en', tracks[0]['language'])
        self.assertEqual({'X-Caption-Token': 'fixture'}, tracks[0]['headers'])

    def test_caption_fallback_uses_public_media_context_without_copying_its_credentials(self):
        page = Page([{'src': 'https://captions.example/en.vtt', 'kind': 'subtitles', 'label': 'English'}])
        with patch.object(scraper, 'is_safe_public_url', return_value=True):
            found = scraper.make_stream_result('https://cdn.example/master.m3u8', page,
                request_headers={'Referer': 'https://player.example/', 'Origin': 'https://player.example',
                    'Cookie': 'media=private', 'Authorization': 'Bearer private'})
        track = found['subtitles'][0]
        self.assertEqual('https://player.example/', track['referer'])
        self.assertEqual({'Referer': 'https://player.example/', 'Origin': 'https://player.example'}, track['headers'])
        self.assertEqual('', track['cookie'])

    def test_caption_own_referrer_and_cookie_override_media_context(self):
        page = Page()
        page._ums_subtitle_tracks = [{'url': 'https://captions.example/en.vtt', 'label': 'English',
            'language': 'en', 'referer': 'https://caption-player.example/', 'cookie': 'caption=own',
            'headers': {'referer': 'https://caption-player.example/', 'cookie': 'caption=own'}}]
        tracks = scraper.collect_subtitle_tracks(page, {'Referer': 'https://player.example/'})
        self.assertEqual('https://caption-player.example/', tracks[0]['referer'])
        self.assertEqual('caption=own', tracks[0]['cookie'])
        self.assertEqual({'referer': 'https://caption-player.example/', 'cookie': 'caption=own'}, tracks[0]['headers'])

    def test_inline_stage_retains_caption_request_context(self):
        page = Page([{'src': '/english.vtt', 'kind': 'subtitles', 'srclang': 'en'}])
        with patch.object(scraper, 'is_safe_public_url', return_value=True):
            found = scraper.stage_d_carpet_bomb(page)
        self.assertEqual('https://player.example/english.vtt', found['subtitles'][0]['url'])
        self.assertEqual(Page.url, found['subtitles'][0]['referer'])
        self.assertEqual('session=fixture', found['subtitles'][0]['cookie'])

    def test_caption_scan_rejects_thumbnails_and_private_or_file_urls(self):
        for item in ({'src': 'https://cdn.example/thumbs.vtt', 'kind': 'thumbnails'},
                     {'src': 'http://127.0.0.1/private.vtt'}, {'src': 'file:///C:/private.srt'}):
            self.assertIsNone(scraper.subtitle_track(item, Page(), Page.url))

    def test_sources_json_discovers_tracks_without_collecting_preview_assets(self):
        config = {'sources': [{'file': 'https://cdn.example/movie.m3u8'}], 'tracks': [
            {'file': '/en.vtt', 'label': 'English', 'kind': 'captions'},
            {'file': '/thumb.vtt', 'kind': 'thumbnails'}]}
        with patch.object(scraper, 'is_safe_public_url', return_value=True):
            found = scraper.config_subtitle_tracks(config, Page(), Page.url)
        self.assertEqual(1, len(found))
        self.assertEqual('https://player.example/en.vtt', found[0]['url'])


if __name__ == '__main__':
    unittest.main()
