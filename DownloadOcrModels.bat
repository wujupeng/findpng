@echo off
chcp 65001 >nul
echo ==============================================
echo       Download PaddleOCR Models
echo       (Package with application)
echo ==============================================
echo.

set "MODEL_DIR=%~dp0OcrModels"
set "TMP_DIR=%TEMP%\PaddleOCR_Download"

echo Creating directories...
mkdir "%MODEL_DIR%" 2>nul
mkdir "%TMP_DIR%" 2>nul

echo.
echo Downloading detection model (ch_PP-OCRv3_det_infer)...
powershell -Command "Invoke-WebRequest -Uri 'https://github.com/PaddlePaddle/PaddleOCR/releases/download/v2.7.0/ch_PP-OCRv3_det_infer.tar' -OutFile '%TMP_DIR%\det.tar'"

echo.
echo Downloading recognition model (ch_PP-OCRv3_rec_infer)...
powershell -Command "Invoke-WebRequest -Uri 'https://github.com/PaddlePaddle/PaddleOCR/releases/download/v2.7.0/ch_PP-OCRv3_rec_infer.tar' -OutFile '%TMP_DIR%\rec.tar'"

echo.
echo Downloading classification model (ch_ppocr_mobile_v2.0_cls_infer)...
powershell -Command "Invoke-WebRequest -Uri 'https://github.com/PaddlePaddle/PaddleOCR/releases/download/v2.7.0/ch_ppocr_mobile_v2.0_cls_infer.tar' -OutFile '%TMP_DIR%\cls.tar'"

echo.
echo Extracting models...
powershell -Command "tar -xf '%TMP_DIR%\det.tar' -C '%MODEL_DIR%'"
powershell -Command "tar -xf '%TMP_DIR%\rec.tar' -C '%MODEL_DIR%'"
powershell -Command "tar -xf '%TMP_DIR%\cls.tar' -C '%MODEL_DIR%'"

echo.
echo Cleaning up...
rmdir /s /q "%TMP_DIR%"

echo.
echo ==============================================
echo              Download Complete!
echo ==============================================
echo Models installed to: %MODEL_DIR%
echo.
echo Directory structure:
echo   OcrModels/
echo     ch_PP-OCRv3_det_infer/
echo     ch_PP-OCRv3_rec_infer/
echo     ch_ppocr_mobile_v2.0_cls_infer/
echo.
echo The models will be automatically detected by the application.
echo.
pause
