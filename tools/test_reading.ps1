#requires -Version 7
<#
Проверка тренажёра чтения целиком (кнопка «Готово»: разбор записи → профиль → обучение слуха) на КОПИИ профилей.
Настоящие data\speakers не трогаются: копия во временной папке, модели — ссылкой (junction) на models.
«Чтение» склеивается из записей человека, у которого их больше всех, с вклейками чужих голосов — в профиль должен
попасть только он. Итог: отчёт тренажёра в консоль и картинка окна.

    pwsh -File tools\test_reading.ps1 [-Exe путь\KlavaDesk.exe]
#>
param([string]$Exe = (Join-Path $PSScriptRoot '..\bin\KlavaDesk.exe'))
$ErrorActionPreference = 'Stop'
$neko = Resolve-Path (Join-Path $PSScriptRoot '..')
$root = Join-Path ([IO.Path]::GetTempPath()) 'klava_reading_test'
$models = Join-Path $root 'models'

function Remove-TestRoot {
    # сначала ссылку на модели (удаляется сама ссылка, не содержимое), потом остальное
    if (Test-Path $models) { (Get-Item $models -Force).Delete() }
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
}

Remove-TestRoot
New-Item -ItemType Directory -Path (Join-Path $root 'data\logs') -Force | Out-Null
Copy-Item (Join-Path $neko 'klava.json') $root
New-Item -ItemType Junction -Path $models -Target (Join-Path $neko 'models') | Out-Null
robocopy (Join-Path $neko 'data\speakers') (Join-Path $root 'data\speakers') /E /XD _карантин /NFL /NDL /NJH /NJS /NP | Out-Null

$png = Join-Path $root 'reading_full.png'
$out = Join-Path $root 'out.txt'
$env:KLAVA_HOME = $root
try {
    $p = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList '--ui-shot', 'reading-full', $png -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
    Get-Content $out -Encoding utf8
    $code = $p.ExitCode
    if (Test-Path $png) {
        $keep = Join-Path ([IO.Path]::GetTempPath()) 'klava_reading_full.png'
        Copy-Item $png $keep -Force
        "картинка: $keep"
    }
}
finally {
    Remove-Item Env:KLAVA_HOME -ErrorAction SilentlyContinue
    Remove-TestRoot
}
"код выхода: $code"
exit $code
