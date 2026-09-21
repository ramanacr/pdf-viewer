@echo off
set "TARGET_FILE=%~1"
if "%TARGET_FILE%"=="" set "TARGET_FILE=%~dp0samples\EngineShowcase.pdf"
echo Starting PDF Viewer with "%TARGET_FILE%"...
start "" "%~dp0src\PdfViewer\bin\Debug\net9.0-windows10.0.19041.0\PdfViewer.exe" "%TARGET_FILE%"
