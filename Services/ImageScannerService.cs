using ImageSearch.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ImageSearch.Services
{
    public class ImageScannerService
    {
        private readonly DatabaseService _dbService;
        private readonly OcrService _ocrService;
        private readonly string[] _supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif" };
        private CancellationTokenSource? _cancellationTokenSource;
        private int _processedCount;
        private int _totalCount;
        
        // 工业级配置
        private const int OCR_WORKER_COUNT = 4;           // 固定OCR线程数
        private const int QUEUE_MAX_CAPACITY = 1000;      // 队列最大缓存
        private const int BATCH_WRITE_SIZE = 100;         // 批量写入大小
        private const long MEMORY_THRESHOLD = 1_500_000_000; // 内存压力阈值(1.5GB)

        public event EventHandler<ScanProgressEventArgs>? ProgressChanged;
        public event EventHandler? ScanCompleted;

        public ImageScannerService(DatabaseService dbService, OcrService ocrService)
        {
            _dbService = dbService;
            _ocrService = ocrService;
        }

        public async Task ScanAsync(string rootDirectory, bool skipExisting = true)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _processedCount = 0;

            try
            {
                var imageFiles = GetAllImageFiles(rootDirectory);
                _totalCount = imageFiles.Count;

                OnProgressChanged(0, "正在初始化OCR引擎...");
                _ocrService.Initialize();

                OnProgressChanged(0, "开始扫描图片...");

                // 创建带容量限制的阻塞队列
                var imageQueue = new BlockingCollection<string>(QUEUE_MAX_CAPACITY);
                var resultQueue = new BlockingCollection<ImageIndexResult>(QUEUE_MAX_CAPACITY);
                
                // 创建OCR信号量控制并发
                var ocrSemaphore = new SemaphoreSlim(OCR_WORKER_COUNT, OCR_WORKER_COUNT);

                // 启动DB写入线程（单线程）
                var dbWriterTask = Task.Run(() => DatabaseWriter(resultQueue, _cancellationTokenSource.Token));

                // 启动OCR Worker池
                var ocrTasks = new List<Task>();
                for (int i = 0; i < OCR_WORKER_COUNT; i++)
                {
                    ocrTasks.Add(Task.Run(() => OcrWorker(imageQueue, resultQueue, ocrSemaphore, skipExisting, _cancellationTokenSource.Token)));
                }

                // Producer: 扫描线程
                await Task.Run(() =>
                {
                    foreach (var filePath in imageFiles)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            break;

                        // 内存压力控制
                        CheckMemoryPressure();

                        // 阻塞直到队列有空间
                        imageQueue.Add(filePath, _cancellationTokenSource.Token);
                    }
                    imageQueue.CompleteAdding();
                });

                // 等待所有OCR Worker完成
                await Task.WhenAll(ocrTasks);
                resultQueue.CompleteAdding();

                // 等待DB写入完成
                await dbWriterTask;

                OnProgressChanged(100, "索引建立完成");
                ScanCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                OnProgressChanged(_processedCount, "扫描已取消");
            }
            catch (Exception ex)
            {
                OnProgressChanged(_processedCount, $"扫描出错: {ex.Message}");
            }
        }

        public void CancelScan()
        {
            _cancellationTokenSource?.Cancel();
        }

        private void CheckMemoryPressure()
        {
            if (GC.GetTotalMemory(false) > MEMORY_THRESHOLD)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
        }

        private async Task OcrWorker(
            BlockingCollection<string> imageQueue,
            BlockingCollection<ImageIndexResult> resultQueue,
            SemaphoreSlim ocrSemaphore,
            bool skipExisting,
            CancellationToken token)
        {
            try
            {
                foreach (var filePath in imageQueue.GetConsumingEnumerable(token))
                {
                    await ocrSemaphore.WaitAsync(token);
                    try
                    {
                        var result = ProcessImage(filePath, skipExisting);
                        if (result != null && !string.IsNullOrWhiteSpace(result.OcrText))
                        {
                            resultQueue.Add(result, token);
                        }

                        var current = Interlocked.Increment(ref _processedCount);
                        var progress = (int)((current / (double)_totalCount) * 100);
                        OnProgressChanged(progress, $"已处理 {current}/{_totalCount}");
                    }
                    finally
                    {
                        ocrSemaphore.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task DatabaseWriter(BlockingCollection<ImageIndexResult> resultQueue, CancellationToken token)
        {
            var batch = new List<ImageIndexResult>();

            try
            {
                foreach (var result in resultQueue.GetConsumingEnumerable(token))
                {
                    batch.Add(result);

                    if (batch.Count >= BATCH_WRITE_SIZE)
                    {
                        await _dbService.BatchInsertImageIndex(batch);
                        batch.Clear();
                    }
                }

                // 处理剩余数据
                if (batch.Count > 0)
                {
                    await _dbService.BatchInsertImageIndex(batch);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private List<string> GetAllImageFiles(string rootDirectory)
        {
            var files = new List<string>();

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
                {
                    files.AddRange(GetAllImageFiles(directory));
                }

                var imageFiles = Directory.EnumerateFiles(rootDirectory)
                    .Where(file => _supportedExtensions.Contains(Path.GetExtension(file).ToLower()))
                    .ToList();
                
                // 记录找到的图片文件
                if (imageFiles.Count > 0)
                {
                    LogMessage($"Found {imageFiles.Count} images in {rootDirectory}");
                    foreach (var img in imageFiles.Take(5)) // 最多记录5个
                    {
                        LogMessage($"  - {Path.GetFileName(img)}");
                    }
                }
                
                files.AddRange(imageFiles);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogMessage($"Access denied: {rootDirectory}");
            }

            return files;
        }

        private void LogMessage(string message)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageSearch", "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                var logPath = Path.Combine(logDir, "scan_log.txt");
                using var writer = new StreamWriter(logPath, true);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            }
            catch { }
        }

        private ImageIndexResult? ProcessImage(string filePath, bool skipExisting)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileMd5 = CalculateMd5(filePath);

                LogMessage($"Processing: {Path.GetFileName(filePath)}");

                if (skipExisting)
                {
                    if (_dbService.IsMd5Exists(fileMd5))
                    {
                        LogMessage($"  - Skipped: MD5 exists in database");
                        return null;
                    }

                    if (_dbService.IsFilePathExists(filePath))
                    {
                        var storedLastModified = _dbService.GetLastModified(filePath);
                        if (storedLastModified.HasValue && 
                            storedLastModified.Value >= fileInfo.LastWriteTime)
                        {
                            LogMessage($"  - Skipped: File exists and not modified");
                            return null;
                        }
                    }
                }

                LogMessage($"  - Running OCR...");
                var ocrText = _ocrService.RecognizeText(filePath);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    LogMessage($"  - OCR Result: {ocrText.Substring(0, Math.Min(50, ocrText.Length))}...");
                    return new ImageIndexResult
                    {
                        FilePath = filePath,
                        OcrText = ocrText,
                        Md5 = fileMd5,
                        LastModified = fileInfo.LastWriteTime
                    };
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private string CalculateMd5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        private void OnProgressChanged(int progress, string message)
        {
            ProgressChanged?.Invoke(this, new ScanProgressEventArgs(progress, message));
        }
    }

    public class ImageIndexResult
    {
        public string FilePath { get; set; } = string.Empty;
        public string OcrText { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
    }

    public class ScanProgressEventArgs : EventArgs
    {
        public int Progress { get; }
        public string Message { get; }

        public ScanProgressEventArgs(int progress, string message)
        {
            Progress = progress;
            Message = message;
        }
    }
}