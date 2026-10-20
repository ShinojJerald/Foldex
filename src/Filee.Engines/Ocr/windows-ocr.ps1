# Text recognition with the OCR engine built into Windows 10 / 11 (Windows.Media.Ocr), for Filee's OCR tools.
# Reads image paths (one per line) from -List, writes one JSON array to standard output: per image its pixel size and
# the recognised lines with word boxes in pixels. -Languages lists the recognition languages Windows has instead.
# Nothing leaves the computer: the engine runs locally on the language packs installed in Windows.
param(
    [string]$List,
    [string]$Language,
    [switch]$Languages
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

if ($Languages) {
    ConvertTo-Json -Compress -InputObject @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object {
        [ordered]@{ tag = $_.LanguageTag; name = $_.DisplayName }
    })
    return
}

$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]

# WinRT async operations as .NET tasks (Windows PowerShell can't await them directly).
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await($operation, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    $null = $task.Wait(-1)
    $task.Result
}

$engine = if ($Language) {
    [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($Language))
} else {
    [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
}
if ($null -eq $engine) { throw "Windows has no text recognition for language '$Language'." }

$results = foreach ($image in (Get-Content -LiteralPath $List -Encoding UTF8 | Where-Object { $_ })) {
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($image)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    try {
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $ocr = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        [ordered]@{
            width  = $bitmap.PixelWidth
            height = $bitmap.PixelHeight
            lines  = @($ocr.Lines | ForEach-Object {
                [ordered]@{
                    text  = $_.Text
                    words = @($_.Words | ForEach-Object {
                        $r = $_.BoundingRect
                        [ordered]@{ text = $_.Text; x = $r.X; y = $r.Y; w = $r.Width; h = $r.Height }
                    })
                }
            })
        }
        $bitmap.Dispose()
    }
    finally {
        $stream.Dispose()
    }
}
ConvertTo-Json -Compress -Depth 6 -InputObject @($results)
