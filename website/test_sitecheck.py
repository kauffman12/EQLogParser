"""Indexing policy checks: run with python -m unittest test_sitecheck."""

import tempfile
import unittest
from pathlib import Path

from bs4 import BeautifulSoup

from sitecheck import NON_INDEXABLE, _indexing_problems, _robots_problems, _sitemap_problems


class IndexingPolicyTest(unittest.TestCase):
    def test_public_pages_allow_indexing_by_default(self):
        for markup in ('', '<meta name="robots" content="index, follow">'):
            with self.subTest(markup=markup):
                self.assertEqual([], self.indexing_problems('faq.html', markup))

    def test_public_noindex_is_rejected_for_each_search_engine(self):
        for agent in ('robots', 'googlebot', 'bingbot'):
            for directive in ('NOINDEX, follow', 'none'):
                with self.subTest(agent=agent, directive=directive):
                    markup = f'<meta name="{agent}" content="{directive}">'
                    self.assertTrue(self.indexing_problems('faq.html', markup))

    def test_utility_pages_need_a_generic_noindex_tag(self):
        for page in sorted(NON_INDEXABLE):
            with self.subTest(page=page):
                self.assertEqual([], self.indexing_problems(page, '<meta name="robots" content="noindex, follow">'))
                self.assertTrue(self.indexing_problems(page, ''))
                self.assertTrue(self.indexing_problems(page, '<meta name="googlebot" content="noindex">'))

    def test_crawlable_status_page_allows_reading_its_noindex(self):
        with tempfile.TemporaryDirectory() as directory:
            dist = Path(directory)
            (dist / 'status.html').touch()
            (dist / 'faq.html').touch()
            (dist / 'robots.txt').write_text('User-agent: *\nAllow: /\n', encoding='utf-8')
            self.assertEqual([], _robots_problems(dist))

            (dist / 'robots.txt').write_text('User-agent: *\nDisallow: /status.html\n', encoding='utf-8')
            self.assertTrue(any('/status.html' in problem for problem in _robots_problems(dist)))

    def test_agent_specific_public_page_blocks_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            dist = Path(directory)
            (dist / 'faq.html').touch()
            (dist / 'robots.txt').write_text(
                'User-agent: Googlebot\nDisallow: /faq.html\n\nUser-agent: *\nAllow: /\n', encoding='utf-8')
            self.assertTrue(any('Googlebot from /faq.html' in problem for problem in _robots_problems(dist)))

    def test_home_page_root_must_be_crawlable(self):
        with tempfile.TemporaryDirectory() as directory:
            dist = Path(directory)
            (dist / 'robots.txt').write_text('User-agent: *\nDisallow: /\n', encoding='utf-8')
            self.assertTrue(_robots_problems(dist))

    def test_utility_pages_cannot_be_advertised_in_the_sitemap(self):
        with tempfile.TemporaryDirectory() as directory:
            dist = Path(directory)
            (dist / 'status.html').touch()
            (dist / 'sitemap.xml').write_text(
                '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'
                '<url><loc>https://eqlogparser.kizant.net/status.html</loc></url></urlset>', encoding='utf-8')
            self.assertTrue(any('must stay out of search' in problem for problem in _sitemap_problems(dist)))

    @staticmethod
    def indexing_problems(page, markup):
        return _indexing_problems(Path(page), BeautifulSoup(markup, 'html.parser'))


if __name__ == '__main__':
    unittest.main()
