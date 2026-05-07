using ImageSearch.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ImageSearch.Services
{
    public class SearchService
    {
        private readonly DatabaseService _dbService;

        public SearchService(DatabaseService dbService)
        {
            _dbService = dbService;
        }

        public SearchResult Search(string query)
        {
            var stopwatch = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(query))
            {
                return new SearchResult(new List<string>(), 0);
            }

            var results = new List<string>();

            try
            {
                // 首先尝试FTS5全文搜索（精确匹配）
                var ftsResults = _dbService.SearchByOcrText(query);
                
                if (ftsResults.Count > 0)
                {
                    results.AddRange(ftsResults);
                }
                else
                {
                    // 如果没有结果，尝试模糊搜索
                    var fuzzyResults = _dbService.SearchByFuzzyLike(query);
                    results.AddRange(fuzzyResults);
                }
            }
            catch (Exception)
            {
                // 如果FTS5失败，回退到LIKE搜索
                results.AddRange(_dbService.SearchByFuzzyLike(query));
            }

            stopwatch.Stop();

            return new SearchResult(results, stopwatch.ElapsedMilliseconds);
        }

        public SearchResult SearchFuzzy(string query)
        {
            var stopwatch = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(query))
            {
                return new SearchResult(new List<string>(), 0);
            }

            var results = _dbService.SearchByFuzzyLike(query);

            stopwatch.Stop();

            return new SearchResult(results, stopwatch.ElapsedMilliseconds);
        }
    }

    public class SearchResult
    {
        public List<string> FilePaths { get; }
        public long SearchTimeMs { get; }

        public SearchResult(List<string> filePaths, long searchTimeMs)
        {
            FilePaths = filePaths;
            SearchTimeMs = searchTimeMs;
        }
    }
}