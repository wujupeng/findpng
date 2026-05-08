using ImageSearch.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
                // 首先尝试LIKE模糊搜索（确保数字能被正确搜索）
                var fuzzyResults = _dbService.SearchByFuzzyLike(query);
                
                LogSearchAttempt($"Query: {query}, LIKE results: {fuzzyResults.Count}");
                
                if (fuzzyResults.Count > 0)
                {
                    results.AddRange(fuzzyResults);
                    // 记录实际返回的文件路径
                    foreach (var path in fuzzyResults.Take(3)) // 最多记录3个
                    {
                        LogSearchAttempt($"  - Path: {path}, Exists: {File.Exists(path)}");
                    }
                }
                else
                {
                    // 如果没有结果，尝试FTS5全文搜索
                    var ftsResults = _dbService.SearchByOcrText(query, true);
                    LogSearchAttempt($"Query: {query}, FTS results: {ftsResults.Count}");
                    results.AddRange(ftsResults);
                }
            }
            catch (Exception ex)
            {
                // 如果都失败，回退到LIKE搜索
                LogSearchAttempt($"Search error: {ex.Message}");
                results.AddRange(_dbService.SearchByFuzzyLike(query));
            }

            LogSearchAttempt($"Final results count: {results.Count}");
            stopwatch.Stop();

            return new SearchResult(results, stopwatch.ElapsedMilliseconds);
        }

        private void LogSearchAttempt(string message)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageSearch", "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                var logPath = Path.Combine(logDir, "search_log.txt");
                using var writer = new StreamWriter(logPath, true);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            }
            catch { }
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