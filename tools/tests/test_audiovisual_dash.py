"""DASH qualification must inspect selected init/media bytes without weakening item evidence."""
import importlib.util
from pathlib import Path
import sys
import time
import unittest
from unittest.mock import patch

CORE = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core'
sys.path.insert(0, str(CORE))
spec = importlib.util.spec_from_file_location('av_dash', CORE / 'audiovisual_scraper.py')
av = importlib.util.module_from_spec(spec)
spec.loader.exec_module(av)

URL = 'https://cdn.example/path/video.mpd'
MANIFEST = b'''<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S">
<Period><AdaptationSet mimeType="video/mp4"><SegmentTemplate timescale="1" duration="2" startNumber="7"
initialization="init-$RepresentationID$.mp4" media="$RepresentationID$/$Number%05d$-$Time$.m4s"/>
<Representation id="low" bandwidth="1"/><Representation id="v" bandwidth="2"/></AdaptationSet>
<AdaptationSet mimeType="audio/mp4" lang="en"><Representation id="a" bandwidth="1">
<BaseURL>https://other.example/audio/</BaseURL><SegmentTemplate timescale="1" initialization="init.mp4" media="$Time$.m4s">
<SegmentTimeline><S t="0" d="2" r="-1"/></SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period></MPD>'''
MP4 = b'\x00\x00\x00\x18ftyp' + b'isom' + b'\x00'*500


class Response:
    def __init__(self, url, body, status=200, headers=None):
        self.url, self.body, self.status_code = url, body, status
        self.headers = headers or {}
        self.closed = False

    def iter_content(self, chunk_size):
        yield self.body

    def close(self):
        self.closed = True


class DashQualificationTests(unittest.TestCase):
    def validate(self, manifest=MANIFEST, bad_payload=None, deadline=None):
        calls, responses = [], []
        def get(url, **kwargs):
            calls.append((url, kwargs))
            body = manifest if url == URL else MP4
            if bad_payload and '/audio/' in url and url.endswith('0.m4s'):
                body = bad_payload
            response = Response(url, body, headers={'content-type': 'video/mp4'})
            responses.append(response)
            return response
        result = av.stream_result(URL, headers={'Cookie': 'session=private', 'Authorization': 'Bearer private', 'X-Playback-Key': 'private'})
        with patch.object(av.cffi_req, 'get', side_effect=get):
            output = av.validated_stream(result, deadline or time.monotonic()+12)
        return output, calls, responses

    def test_init_and_media_are_checked_for_selected_video_and_audio_with_scoped_headers(self):
        result, calls, responses = self.validate()
        self.assertTrue(result['media_validated'])
        self.assertEqual([], result['audio_languages'])
        self.assertEqual('application/dash+xml', result['content_type'])
        self.assertEqual([URL, 'https://cdn.example/path/init-v.mp4', 'https://cdn.example/path/v/00007-0.m4s',
                          'https://other.example/audio/init.mp4', 'https://other.example/audio/0.m4s'], [url for url, _ in calls])
        self.assertTrue(all(response.closed for response in responses))
        self.assertFalse(any('/low/' in url for url, _ in calls))
        self.assertIn('Cookie', calls[1][1]['headers'])
        for _, kwargs in calls[3:]:
            self.assertFalse(any(key.lower() in ('cookie', 'authorization', 'x-playback-key') for key in kwargs['headers']))
            self.assertEqual('bytes=0-65535', kwargs['headers']['Range'])
            self.assertFalse(kwargs['allow_redirects'])

    def test_video_label_and_large_xml_or_audio_decoy_never_establish_media(self):
        for payload in (b'<html>'+b'x'*10000, b'\x00\x00\x00\x01ftyp'+b'x'*1000, b'', b'\x00\x00\x00\x10xxxx'+b'x'*1000):
            with self.subTest(payload=payload[:8]):
                result, _, _ = self.validate(bad_payload=payload or b' ')
                self.assertIsNone(result)

    def test_unsupported_unsafe_or_incomplete_manifests_never_check_media(self):
        mutations = [
            MANIFEST.replace(b'type="static"', b'type="dynamic"'),
            MANIFEST.replace(b'<Period>', b'<Period><ContentProtection/>'),
            b'<!DOCTYPE MPD [<!ENTITY test SYSTEM "file:///private">]>'+MANIFEST,
            MANIFEST.replace(b'<Period>', b'<Period xmlns:xlink="http://www.w3.org/1999/xlink" xlink:href="https://other.example/xml">'),
            MANIFEST.replace(b'</MPD>', b'<Period/></MPD>'),
            MANIFEST.replace(b't="0"', b't="1"'),
            MANIFEST.replace(b'PT4S', b'PT999999S'),
            MANIFEST.replace(b'initialization="init.mp4"', b'initialization="http://127.0.0.1/private.mp4"'),
            MANIFEST.replace(b'$Number%05d$', b'$Unknown$'),
            MANIFEST.replace(b'timescale="1"', b'presentationTimeOffset="1" timescale="1"'),
            MANIFEST.replace(b'video/mp4', b'video/webm'),
            MANIFEST.replace(b'<Period>', b'<Period><SegmentBase/>'),
            MANIFEST.replace(b'<Period>', b'<Period><SegmentList/>'),
            b'<MPD>'+b'x'*(2*1024*1024),
        ]
        for manifest in mutations:
            with self.subTest(manifest=manifest[:100]):
                result, calls, responses = self.validate(manifest)
                self.assertIsNone(result)
                self.assertEqual(1, len(calls))
                self.assertTrue(all(response.closed for response in responses))

    def test_redirect_strips_credentials_and_private_redirect_is_never_requested(self):
        calls = []
        def get(url, **kwargs):
            calls.append((url, kwargs))
            if url == URL:
                return Response(url, b'', 302, {'location': 'https://other.example/video.mpd'})
            return Response(url, MANIFEST if url.endswith('.mpd') else MP4)
        headers = {'Cookie': 'private', 'Authorization': 'private', 'User-Agent': 'Agent'}
        with patch.object(av.cffi_req, 'get', side_effect=get):
            data, effective = av.dash_fetch_bytes(URL, headers, URL, 2*1024*1024, time.monotonic()+5)
        self.assertEqual(MANIFEST, data)
        self.assertEqual('https://other.example/video.mpd', effective)
        self.assertEqual({'User-Agent': 'Agent'}, calls[1][1]['headers'])
        calls.clear()
        with patch.object(av.cffi_req, 'get', return_value=Response(URL, b'', 302, {'location': 'http://127.0.0.1/private'})) as mocked:
            with self.assertRaises(ValueError):
                av.dash_fetch_bytes(URL, headers, URL, 2*1024*1024, time.monotonic()+5)
        self.assertEqual(1, mocked.call_count)

    def test_expired_candidate_and_wrong_partial_range_do_not_pass(self):
        with patch.object(av.cffi_req, 'get') as get:
            self.assertIsNone(av.validated_stream(av.stream_result(URL), time.monotonic()-1))
            get.assert_not_called()
        with patch.object(av.cffi_req, 'get', return_value=Response(URL, MP4, 206, {'content-range': 'bytes 100-199/500'})):
            with self.assertRaises(ValueError):
                av.dash_fetch_bytes(URL, {}, URL, 65536, time.monotonic()+5, sample=True)
        with patch.object(av.cffi_req, 'get', return_value=Response(URL, MP4, headers={'content-length': '1000'})):
            with self.assertRaises(ValueError):
                av.dash_fetch_bytes(URL, {}, URL, 65536, time.monotonic()+5, sample=True)


if __name__ == '__main__':
    unittest.main()
