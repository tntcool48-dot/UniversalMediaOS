"""Book search evidence must come from the individual card, not a query or nearby result."""
import importlib.util
from pathlib import Path
import unittest

CORE = Path(__file__).resolve().parents[2] / 'UniversalMediaOS.Core'
spec = importlib.util.spec_from_file_location('books_evidence', CORE / 'book_scraper.py')
books = importlib.util.module_from_spec(spec)
spec.loader.exec_module(books)


class BookItemEvidenceTests(unittest.TestCase):
    def test_isbn_and_publisher_stay_with_their_own_card(self):
        html = '''<div class="search-result"><a href="/book/opaque"><h3>Matilda</h3></a>
          <span itemprop="author">Roald Dahl</span><span itemprop="encodingFormat">epub</span>
          <span itemprop="isbn">0-14-032872-6</span><span itemprop="publisher">Puffin</span>
          <span itemprop="datePublished">1988</span><span itemprop="inLanguage">English</span></div>
          <div class="search-result"><a href="/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"><h3>Another book</h3></a>
          <span itemprop="encodingFormat">pdf</span><span itemprop="isbn">9780131103627</span></div>'''
        first, second = books.parse_search_html(html)
        self.assertEqual(['0-14-032872-6'], first['isbns'])
        self.assertEqual('Puffin', first['publisher'])
        self.assertEqual('', first['md5'])
        self.assertEqual(['9780131103627'], second['isbns'])
        self.assertEqual('a'*32, second['md5'])

    def test_label_isbn_is_independent_and_missing_isbn_stays_empty(self):
        html = '''<div class="search-result"><a href="/book/first"><h3>Title</h3></a>
          File: epub ISBN-13: 978-0-14-032872-1 Publisher: Puffin Year: 1988 Language: English</div>
          <div class="search-result"><a href="/book/second"><h3>9780140328721</h3></a>
          File: pdf Year: 1988</div>'''
        first, second = books.parse_search_html(html)
        self.assertEqual(['978-0-14-032872-1'], first['isbns'])
        self.assertEqual('Puffin', first['publisher'])
        self.assertEqual([], second['isbns'])
        self.assertEqual('', second['md5'])

    def test_description_years_and_language_numbers_are_not_isbn_evidence(self):
        html = '''<div class="search-result"><a href="/book/first"><h3>1984</h3></a>
          <span itemprop="encodingFormat">epub</span><p>ISBN coverage 9780140328721 in this book.</p>
          <span itemprop="datePublished">1949</span></div>'''
        self.assertEqual([], books.parse_search_html(html)[0]['isbns'])


if __name__ == '__main__':
    unittest.main()
