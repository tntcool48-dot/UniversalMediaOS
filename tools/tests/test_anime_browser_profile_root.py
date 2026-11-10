"""The anime resolver's Chromium profile belongs to the run folder its C# caller owns and removes."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('anime_profile_scraper',
    Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core/scraper.py')
scraper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scraper)


class AnimeBrowserProfileRootTests(unittest.TestCase):
    def launch(self, environment):
        options = Mock()
        with patch.dict(os.environ, environment), \
                patch.object(scraper, 'DRISSION_AVAILABLE', True), \
                patch.object(scraper, 'ChromiumOptions', Mock(return_value=options), create=True), \
                patch.object(scraper, 'ChromiumPage', Mock(return_value=Mock()), create=True), \
                patch.object(scraper, 'hide_browser_windows'), \
                patch.object(scraper, 'start_browser_hide_monitor', return_value=None):
            return scraper.launch_browser(), options

    def test_profile_is_created_inside_the_run_folder_the_caller_owns(self):
        with tempfile.TemporaryDirectory() as owned:
            run_root = os.path.join(owned, 'umos_scraper_run_fixture')
            page, options = self.launch({'UMOS_SCRAPER_PROFILE_ROOT': run_root})
            self.assertEqual(os.path.normcase(run_root), os.path.normcase(os.path.dirname(page._ums_tmp_dir)))
            options.set_user_data_path.assert_called_once_with(page._ums_tmp_dir)

    def test_a_relative_run_folder_is_refused(self):
        with self.assertRaises(ValueError):
            self.launch({'UMOS_SCRAPER_PROFILE_ROOT': 'relative/run'})


if __name__ == '__main__':
    unittest.main()
