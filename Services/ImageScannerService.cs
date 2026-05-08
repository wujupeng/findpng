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
        
        private const int OCR_WORKER_COUNT = 4;
        private const int QUEUE_MAX_CAPACITY = 1000;
        private const int BATCH_WRITE_SIZE = 100;
        private const long MEMORY_THRESHOLD = 1_500_000_000;

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

                var imageQueue = new BlockingCollection<string>(QUEUE_MAX_CAPACITY);
                var resultQueue = new BlockingCollection<ImageIndexResult>(QUEUE_MAX_CAPACITY);
                
                var ocrSemaphore = new SemaphoreSlim(OCR_WORKER_COUNT, OCR_WORKER_COUNT);

                var dbWriterTask = Task.Run(() => DatabaseWriter(resultQueue, _cancellationTokenSource.Token));

                var ocrTasks = new List<Task>();
                for (int i = 0; i < OCR_WORKER_COUNT; i++)
                {
                    ocrTasks.Add(Task.Run(() => OcrWorker(imageQueue, resultQueue, ocrSemaphore, skipExisting, _cancellationTokenSource.Token)));
                }

                await Task.Run(() =>
                {
                    foreach (var filePath in imageFiles)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            break;

                        CheckMemoryPressure();

                        imageQueue.Add(filePath, _cancellationTokenSource.Token);
                    }
                    imageQueue.CompleteAdding();
                });

                await Task.WhenAll(ocrTasks);
                resultQueue.CompleteAdding();

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
                
                if (imageFiles.Count > 0)
                {
                    LogMessage($"Found {imageFiles.Count} images in {rootDirectory}");
                    foreach (var img in imageFiles.Take(5))
                    {
                        LogMessage($"  - {Path.GetFileName(img)}");
                    }
                }
                
                files.AddRange(imageFiles);
            }
            catch (UnauthorizedAccessException)
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
                    LogMessage($"  - OCR Raw: {ocrText.Substring(0, Math.Min(50, ocrText.Length))}...");
                    
                    // 使用OcrPostProcessor处理OCR结果
                    var processor = new OcrPostProcessor();
                    var processed = processor.Process(ocrText);
                    
                    LogMessage($"  - Corrected: {processed.CorrectedText.Substring(0, Math.Min(50, processed.CorrectedText.Length))}...");
                    LogMessage($"  - TailCode: {processed.TailCode}");
                    
                    return new ImageIndexResult
                    {
                        FilePath = filePath,
                        OcrText = processed.CorrectedText,
                        TailCode = processed.TailCode,
                        Md5 = fileMd5,
                        LastModified = fileInfo.LastWriteTime
                    };
                }
            }
            catch (Exception ex)
            {
                LogMessage($"  - Error: {ex.Message}");
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
        public string TailCode { get; set; } = string.Empty;
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