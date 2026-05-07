using OpenCvSharp;
using Sdcb.PaddleOCR;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ImageSearch.Services
{
    public class OcrService : IDisposable
    {
        private PaddleOcrAll? _ocr;
        private bool _isInitialized;
        private readonly object _lock = new object();

        public bool IsInitialized => _isInitialized;

        public void Initialize()
        {
            lock (_lock)
            {
                if (_isInitialized) return;

                try
                {
                    _ocr = new PaddleOcrAll(
                        PaddleOcrModels.Local.EnglishV3,
                        enable_mkldnn: true,
                        cpuThreadNum: Math.Max(1, Environment.ProcessorCount / 2)
                    );
                    _isInitialized = true;
                }
                catch (Exception)
                {
                    _ocr = new PaddleOcrAll(
                        PaddleOcrModels.Local.EnglishV3,
                        enable_mkldnn: false,
                        cpuThreadNum: Math.Max(1, Environment.ProcessorCount / 2)
                    );
                    _isInitialized = true;
                }
            }
        }

        public string RecognizeText(string imagePath)
        {
            if (!_isInitialized || _ocr == null)
            {
                Initialize();
            }

            if (_ocr == null)
                return string.Empty;

            // 使用 using 语句确保所有资源都被正确释放
            using var originalMat = Cv2.ImRead(imagePath, ImreadModes.Color);
            
            if (originalMat.Empty())
                return string.Empty;

            try
            {
                using var grayMat = new Mat();
                Cv2.CvtColor(originalMat, grayMat, ColorConversionCodes.BGR2GRAY);

                using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
                using var enhancedMat = new Mat();
                clahe.Apply(grayMat, enhancedMat);

                using var binaryMat = new Mat();
                Cv2.AdaptiveThreshold(
                    enhancedMat, 
                    binaryMat, 
                    255, 
                    AdaptiveThresholdTypes.GaussianC, 
                    ThresholdTypes.Binary, 
                    11, 
                    2
                );

                using var denoisedMat = new Mat();
                Cv2.MedianBlur(binaryMat, denoisedMat, 3);

                using var kernel = new Mat(3, 3, MatType.CV_32F, new float[] {
                    -1, -1, -1,
                    -1,  9, -1,
                    -1, -1, -1
                });
                using var sharpenedMat = new Mat();
                Cv2.Filter2D(denoisedMat, sharpenedMat, -1, kernel);

                // 转换为Bitmap并立即使用后释放
                using var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(sharpenedMat);
                var result = _ocr.Run(bitmap);
                return CleanOcrResult(result);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string CleanOcrResult(PaddleOcrResult result)
        {
            if (result == null || result.RecognizedTexts == null)
                return string.Empty;

            var allowedChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            
            var cleanedTexts = result.RecognizedTexts
                .Select(block => new string(block.Text.Where(c => allowedChars.Contains(c)).ToArray()))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return string.Join(" ", cleanedTexts);
        }

        public void Dispose()
        {
            _ocr?.Dispose();
            _isInitialized = false;
        }
    }
}