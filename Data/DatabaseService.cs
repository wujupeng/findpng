using ImageSearch.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;

namespace ImageSearch.Data
{
    public class DatabaseService : IDisposable
    {
        private readonly string _dbPath;
        private const string DbFileName = "image_index.db";
        private readonly SqliteConnection _sharedConnection;

        public DatabaseService(string appDataPath)
        {
            _dbPath = Path.Combine(appDataPath, DbFileName);
            
            // 使用连接字符串开启WAL模式，大幅提升并发写入性能
            _sharedConnection = new SqliteConnection(
                $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared;");
            _sharedConnection.Open();
            EnableWalMode();
            InitializeDatabase();
        }

        private void EnableWalMode()
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }

        private void MigrateOldDatabase()
        {
            using var transaction = _sharedConnection.BeginTransaction();
            
            try
            {
                using var dropFtsCmd = _sharedConnection.CreateCommand();
                dropFtsCmd.CommandText = "DROP TABLE IF EXISTS image_fts;";
                dropFtsCmd.ExecuteNonQuery();
                
                using var dropTriggerCmd = _sharedConnection.CreateCommand();
                dropTriggerCmd.CommandText = @"
                    DROP TRIGGER IF EXISTS image_fts_ai;
                    DROP TRIGGER IF EXISTS image_fts_ad;
                    DROP TRIGGER IF EXISTS image_fts_au;";
                dropTriggerCmd.ExecuteNonQuery();
                
                transaction.Commit();
            }
            catch (Exception)
            {
                // 表可能不存在，忽略
            }
            
            using var checkColumnCmd = _sharedConnection.CreateCommand();
            checkColumnCmd.CommandText = @"
                SELECT COUNT(*) FROM pragma_table_info('image_index') WHERE name = 'raw_text';";
            
            try
            {
                var count = (long)checkColumnCmd.ExecuteScalar();
                if (count > 0)
                {
                    using var trans = _sharedConnection.BeginTransaction();
                    
                    using var renameCmd = _sharedConnection.CreateCommand();
                    renameCmd.CommandText = "ALTER TABLE image_index RENAME TO image_index_old;";
                    renameCmd.ExecuteNonQuery();
                    
                    trans.Commit();
                }
            }
            catch (Exception)
            {
                // 表可能不存在，忽略
            }
        }

        private void InitializeDatabase()
        {
            var directory = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            MigrateOldDatabase();

            // 先创建或更新主表
            using var createTableCmd = _sharedConnection.CreateCommand();
            createTableCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS image_index (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path TEXT UNIQUE NOT NULL,
                    ocr_text TEXT NOT NULL,
                    tail_code TEXT DEFAULT '',
                    md5 TEXT NOT NULL,
                    create_time DATETIME NOT NULL,
                    last_modified DATETIME NOT NULL
                );";
            createTableCmd.ExecuteNonQuery();

            // 检查并添加 tail_code 列（针对旧数据库）- 在创建FTS表之前执行
            using var checkTailCodeCmd = _sharedConnection.CreateCommand();
            checkTailCodeCmd.CommandText = @"
                SELECT COUNT(*) FROM pragma_table_info('image_index') WHERE name = 'tail_code';";
            try
            {
                var count = (long)checkTailCodeCmd.ExecuteScalar();
                if (count == 0)
                {
                    using var addColumnCmd = _sharedConnection.CreateCommand();
                    addColumnCmd.CommandText = "ALTER TABLE image_index ADD COLUMN tail_code TEXT DEFAULT '';";
                    addColumnCmd.ExecuteNonQuery();
                }
            }
            catch (Exception)
            {
                // 列可能已存在，忽略
            }

            // 删除旧的FTS表（如果存在），因为它可能引用了不存在的列
            using var dropFtsCmd = _sharedConnection.CreateCommand();
            dropFtsCmd.CommandText = "DROP TABLE IF EXISTS image_fts;";
            dropFtsCmd.ExecuteNonQuery();

            // 删除旧的触发器
            using var dropTriggerCmd = _sharedConnection.CreateCommand();
            dropTriggerCmd.CommandText = @"
                DROP TRIGGER IF EXISTS image_fts_ai;
                DROP TRIGGER IF EXISTS image_fts_ad;
                DROP TRIGGER IF EXISTS image_fts_au;";
            dropTriggerCmd.ExecuteNonQuery();

            // 重新创建FTS表
            using var createFtsCmd = _sharedConnection.CreateCommand();
            createFtsCmd.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS image_fts USING fts5(
                    file_path,
                    ocr_text,
                    tail_code,
                    content='image_index',
                    content_rowid='id'
                );";
            createFtsCmd.ExecuteNonQuery();

            // 重新创建触发器
            using var createTriggerCmd = _sharedConnection.CreateCommand();
            createTriggerCmd.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS image_fts_ai AFTER INSERT ON image_index BEGIN
                    INSERT INTO image_fts(rowid, file_path, ocr_text, tail_code) VALUES (new.id, new.file_path, new.ocr_text, new.tail_code);
                END;
                CREATE TRIGGER IF NOT EXISTS image_fts_ad AFTER DELETE ON image_index BEGIN
                    INSERT INTO image_fts(image_fts, rowid, file_path, ocr_text, tail_code) VALUES ('delete', old.id, old.file_path, old.ocr_text, old.tail_code);
                END;
                CREATE TRIGGER IF NOT EXISTS image_fts_au AFTER UPDATE ON image_index BEGIN
                    INSERT INTO image_fts(image_fts, rowid, file_path, ocr_text, tail_code) VALUES ('delete', old.id, old.file_path, old.ocr_text, old.tail_code);
                    INSERT INTO image_fts(rowid, file_path, ocr_text, tail_code) VALUES (new.id, new.file_path, new.ocr_text, new.tail_code);
                END;";
            createTriggerCmd.ExecuteNonQuery();

            using var createMd5IndexCmd = _sharedConnection.CreateCommand();
            createMd5IndexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_image_index_md5 ON image_index(md5);";
            createMd5IndexCmd.ExecuteNonQuery();

            using var createPathIndexCmd = _sharedConnection.CreateCommand();
            createPathIndexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_image_index_file_path ON image_index(file_path);";
            createPathIndexCmd.ExecuteNonQuery();

            using var createTailCodeIndexCmd = _sharedConnection.CreateCommand();
            createTailCodeIndexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_tail_code ON image_index(tail_code);";
            createTailCodeIndexCmd.ExecuteNonQuery();
        }

        public bool IsMd5Exists(string md5)
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_index WHERE md5 = @md5";
            cmd.Parameters.AddWithValue("@md5", md5);

            return (long)cmd.ExecuteScalar() > 0;
        }

        public bool IsFilePathExists(string filePath)
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_index WHERE file_path = @filePath";
            cmd.Parameters.AddWithValue("@filePath", filePath);

            return (long)cmd.ExecuteScalar() > 0;
        }

        public void InsertImageIndex(string filePath, string ocrText, string tailCode, string md5, DateTime lastModified)
        {
            using var transaction = _sharedConnection.BeginTransaction();

            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO image_index (file_path, ocr_text, tail_code, md5, create_time, last_modified)
                VALUES (@filePath, @ocrText, @tailCode, @md5, @createTime, @lastModified)";
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.Parameters.AddWithValue("@ocrText", ocrText);
            cmd.Parameters.AddWithValue("@tailCode", tailCode ?? "");
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

            using var transaction = await _sharedConnection.BeginTransactionAsync();

            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO image_index (file_path, ocr_text, tail_code, md5, create_time, last_modified)
                VALUES (@filePath, @ocrText, @tailCode, @md5, @createTime, @lastModified)";

            var filePathParam = cmd.CreateParameter();
            filePathParam.ParameterName = "@filePath";
            cmd.Parameters.Add(filePathParam);

            var ocrTextParam = cmd.CreateParameter();
            ocrTextParam.ParameterName = "@ocrText";
            cmd.Parameters.Add(ocrTextParam);

            var tailCodeParam = cmd.CreateParameter();
            tailCodeParam.ParameterName = "@tailCode";
            cmd.Parameters.Add(tailCodeParam);

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
                tailCodeParam.Value = result.TailCode ?? "";
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

            using var cmd = _sharedConnection.CreateCommand();
            
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

        public List<string> SearchByTailCode(string query)
        {
            var results = new List<string>();

            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = @"
                SELECT file_path FROM image_index 
                WHERE tail_code LIKE @pattern 
                ORDER BY id DESC;";
            cmd.Parameters.AddWithValue("@pattern", $"%{query}%");

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

            using var cmd = _sharedConnection.CreateCommand();
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
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM image_index";

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void ClearAll()
        {
            using var transaction = _sharedConnection.BeginTransaction();

            using var deleteFtsCmd = _sharedConnection.CreateCommand();
            deleteFtsCmd.CommandText = "DELETE FROM image_fts";
            deleteFtsCmd.ExecuteNonQuery();

            using var deleteCmd = _sharedConnection.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM image_index";
            deleteCmd.ExecuteNonQuery();

            transaction.Commit();
        }

        public List<string> GetAllFilePaths()
        {
            var results = new List<string>();

            using var cmd = _sharedConnection.CreateCommand();
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
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = "SELECT last_modified FROM image_index WHERE file_path = @filePath";
            cmd.Parameters.AddWithValue("@filePath", filePath);

            var result = cmd.ExecuteScalar();
            return result != DBNull.Value ? (DateTime?)result : null;
        }

        public void Dispose()
        {
            _sharedConnection?.Close();
            _sharedConnection?.Dispose();
        }
    }
}