using ImageSearch.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;

namespace ImageSearch.Data
{
    public class DatabaseService
    {
        private readonly string _dbPath;
        private const string DbFileName = "image_index.db";

        public DatabaseService(string appDataPath)
        {
            _dbPath = Path.Combine(appDataPath, DbFileName);
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            var directory = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            // 创建主表
            using var createTableCmd = connection.CreateCommand();
            createTableCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS image_index (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path TEXT UNIQUE NOT NULL,
                    ocr_text TEXT NOT NULL,
                    md5 TEXT NOT NULL,
                    create_time DATETIME NOT NULL,
                    last_modified DATETIME NOT NULL
                );";
            createTableCmd.ExecuteNonQuery();

            // 创建FTS5全文索引
            using var createFtsCmd = connection.CreateCommand();
            createFtsCmd.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS image_fts USING fts5(
                    file_path,
                    ocr_text,
                    content='image_index',
                    content_rowid='id'
                );";
            createFtsCmd.ExecuteNonQuery();

            // 创建触发器保持FTS索引同步
            using var createTriggerCmd = connection.CreateCommand();
            createTriggerCmd.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS image_fts_ai AFTER INSERT ON image_index BEGIN
                    INSERT INTO image_fts(rowid, file_path, ocr_text) VALUES (new.id, new.file_path, new.ocr_text);
                END;
                CREATE TRIGGER IF NOT EXISTS image_fts_ad AFTER DELETE ON image_index BEGIN
                    INSERT INTO image_fts(image_fts, rowid, file_path, ocr_text) VALUES ('delete', old.id, old.file_path, old.ocr_text);
                END;
                CREATE TRIGGER IF NOT EXISTS image_fts_au AFTER UPDATE ON image_index BEGIN
                    INSERT INTO image_fts(image_fts, rowid, file_path, ocr_text) VALUES ('delete', old.id, old.file_path, old.ocr_text);
                    INSERT INTO image_fts(rowid, file_path, ocr_text) VALUES (new.id, new.file_path, new.ocr_text);
                END;";
            createTriggerCmd.ExecuteNonQuery();

            // 创建MD5索引提高去重查询速度
            using var createMd5IndexCmd = connection.CreateCommand();
            createMd5IndexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_image_index_md5 ON image_index(md5);";
            createMd5IndexCmd.ExecuteNonQuery();

            // 创建file_path索引
            using var createPathIndexCmd = connection.CreateCommand();
            createPathIndexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_image_index_file_path ON image_index(file_path);";
            createPathIndexCmd.ExecuteNonQuery();
        }

        public bool IsMd5Exists(string md5)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_index WHERE md5 = @md5";
            cmd.Parameters.AddWithValue("@md5", md5);

            return (long)cmd.ExecuteScalar() > 0;
        }

        public bool IsFilePathExists(string filePath)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_index WHERE file_path = @filePath";
            cmd.Parameters.AddWithValue("@filePath", filePath);

            return (long)cmd.ExecuteScalar() > 0;
        }

        public void InsertImageIndex(string filePath, string ocrText, string md5, DateTime lastModified)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO image_index (file_path, ocr_text, md5, create_time, last_modified)
                VALUES (@filePath, @ocrText, @md5, @createTime, @lastModified)";
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.Parameters.AddWithValue("@ocrText", ocrText);
            cmd.Parameters.AddWithValue("@md5", md5);
            cmd.Parameters.AddWithValue("@createTime", DateTime.Now);
            cmd.Parameters.AddWithValue("@lastModified", lastModified);

            cmd.ExecuteNonQuery();

            transaction.Commit();
        }

        public async Task BatchInsertImageIndex(List<ImageIndexResult> results)
        {
            if (results == null || results.Count == 0)
                return;

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            using var transaction = await connection.BeginTransactionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO image_index (file_path, ocr_text, md5, create_time, last_modified)
                VALUES (@filePath, @ocrText, @md5, @createTime, @lastModified)";

            var filePathParam = cmd.CreateParameter();
            filePathParam.ParameterName = "@filePath";
            cmd.Parameters.Add(filePathParam);

            var ocrTextParam = cmd.CreateParameter();
            ocrTextParam.ParameterName = "@ocrText";
            cmd.Parameters.Add(ocrTextParam);

            var md5Param = cmd.CreateParameter();
            md5Param.ParameterName = "@md5";
            cmd.Parameters.Add(md5Param);

            var createTimeParam = cmd.CreateParameter();
            createTimeParam.ParameterName = "@createTime";
            cmd.Parameters.Add(createTimeParam);

            var lastModifiedParam = cmd.CreateParameter();
            lastModifiedParam.ParameterName = "@lastModified";
            cmd.Parameters.Add(lastModifiedParam);

            var now = DateTime.Now;

            foreach (var result in results)
            {
                filePathParam.Value = result.FilePath;
                ocrTextParam.Value = result.OcrText;
                md5Param.Value = result.Md5;
                createTimeParam.Value = now;
                lastModifiedParam.Value = result.LastModified;

                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public List<string> SearchByOcrText(string query, bool fuzzy = false)
        {
            var results = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            
            if (fuzzy)
            {
                cmd.CommandText = @"
                    SELECT file_path FROM image_fts 
                    WHERE ocr_text MATCH @query 
                    ORDER BY rank;";
                cmd.Parameters.AddWithValue("@query", $"{query}*");
            }
            else
            {
                cmd.CommandText = @"
                    SELECT file_path FROM image_fts 
                    WHERE ocr_text MATCH @query 
                    ORDER BY rank;";
                cmd.Parameters.AddWithValue("@query", query);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        public List<string> SearchByFuzzyLike(string query)
        {
            var results = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT file_path FROM image_index 
                WHERE ocr_text LIKE @pattern 
                ORDER BY id DESC;";
            cmd.Parameters.AddWithValue("@pattern", $"%{query}%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        public int GetIndexedCount()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_index";

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void ClearAll()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var transaction = connection.BeginTransaction();

            using var deleteFtsCmd = connection.CreateCommand();
            deleteFtsCmd.CommandText = "DELETE FROM image_fts";
            deleteFtsCmd.ExecuteNonQuery();

            using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM image_index";
            deleteCmd.ExecuteNonQuery();

            transaction.Commit();
        }

        public List<string> GetAllFilePaths()
        {
            var results = new List<string>();

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT file_path FROM image_index";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        public DateTime? GetLastModified(string filePath)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT last_modified FROM image_index WHERE file_path = @filePath";
            cmd.Parameters.AddWithValue("@filePath", filePath);

            var result = cmd.ExecuteScalar();
            return result != DBNull.Value ? (DateTime?)result : null;
        }
    }
}