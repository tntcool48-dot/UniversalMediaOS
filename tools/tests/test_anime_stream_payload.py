"""Regressions for the observed TS video mislabeled as image/jpeg."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

SCRIPT = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py'
spec = importlib.util.spec_from_file_location('anime_stream_scraper', SCRIPT)
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)


def transport_stream():
    data = bytearray(188 * 4)
    for i in range(4):
        data[i * 188:i * 188 + 4] = bytes.fromhex('47010010')
    return bytes(data)


class StreamPayloadTests(unittest.TestCase):
    def test_jpeg_content_type_does_not_reject_verified_video(self):
        self.assertTrue(scraper.payload_looks_like_media(transport_stream(), 'image/jpeg'))

    def test_range_response_can_begin_between_transport_packets(self):
        for offset in (0, 17, 187):
            with self.subTest(offset=offset):
                self.assertTrue(scraper.payload_is_mpeg_ts(b'\0' * offset + transport_stream()))

    def test_real_images_stay_rejected_even_with_video_like_bytes_later(self):
        for magic in (b'\xff\xd8\xff', b'\x89PNG', b'GIF8'):
            with self.subTest(magic=magic):
                self.assertFalse(scraper.payload_looks_like_media(magic + transport_stream(), 'image/jpeg'))

    def test_isolated_sync_byte_and_corrupt_packet_are_not_video_evidence(self):
        self.assertFalse(scraper.payload_is_mpeg_ts(b'G' + b'\0' * 2047))
        data = bytearray(transport_stream())
        data[188 * 3] = 0
        self.assertFalse(scraper.payload_is_mpeg_ts(data))
        self.assertFalse(scraper.payload_is_mpeg_ts(transport_stream()[:188 * 3]))

    def test_html_and_json_decoys_remain_rejected(self):
        for content_type in ('text/html', 'application/json'):
            for body in (b'<html>Blocked</html>', b'{"error":"blocked"}'):
                self.assertFalse(scraper.payload_looks_like_media(body + b' ' * 1024, content_type))

    def test_valid_hls_with_mislabeled_segment_keeps_the_native_result(self):
        playlist = Mock(status_code=200, url='https://cdn.example/index.m3u8',
                        headers={'content-type': 'application/vnd.apple.mpegurl'},
                        text='#EXTM3U\n#EXTINF:6,\nsegment.jpg\n')
        segment = Mock(status_code=200, url='https://cdn.example/segment.jpg',
                       headers={'content-type': 'image/jpeg'})
        segment.iter_content.return_value = iter([transport_stream()])
        result = {'url': playlist.url, 'referer': 'https://player.example/watch/1'}
        with patch.object(scraper, 'cffi_req', Mock(get=Mock(side_effect=[playlist, segment]))), \
             patch.object(scraper, 'is_safe_public_url', return_value=True):
            self.assertIs(result, scraper.accept_stream_result(result, 'Initial network'))
        segment.close.assert_called_once()

    def test_real_jpeg_segment_still_uses_the_browser_fallback(self):
        result = {'url': 'https://cdn.example/segment.mp4', 'referer': 'https://player.example/watch/1'}
        segment = Mock(status_code=200, url=result['url'], headers={'content-type': 'image/jpeg'})
        segment.iter_content.return_value = iter([b'\xff\xd8\xff' + b'\0' * 2048])
        with patch.object(scraper, 'cffi_req', Mock(get=Mock(return_value=segment))), \
             patch.object(scraper, 'is_safe_public_url', return_value=True):
            fallback = scraper.accept_stream_result(result, 'Initial network')
        self.assertTrue(fallback['requires_webview'])
        self.assertEqual(result['referer'], fallback['url'])


if __name__ == '__main__':
    unittest.main()
