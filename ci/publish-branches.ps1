# Публикация результатов сборки в отдельные ветки репозитория:
#   build            — установщик, portable-архив, COMMIT (только при успешной сборке);
#   ui-test-results  — результаты тестов, скриншоты, экспорт PDF/HTML;
#   ci-logs-build    — журналы всех шагов (при ошибке — в первую очередь).
param([string]$Status = "unknown")
$ErrorActionPreference = "Continue"
$sha = $env:GITHUB_SHA
$repo = $env:GITHUB_REPOSITORY
$url = "https://x-access-token:$($env:GH_TOKEN)@github.com/$repo.git"
$root = (Get-Location).Path
$info = @(
  "commit=$sha",
  "run=$($env:GITHUB_RUN_NUMBER)",
  "status=$Status",
  "version=$($env:VERSION)",
  "date=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC",
  "url=https://github.com/$repo/actions/runs/$($env:GITHUB_RUN_ID)"
) -join "`n"

function Publish-Branch([string]$branch, [string]$dir, [string]$message) {
  Set-Content -Path (Join-Path $dir "COMMIT") -Value $info -Encoding utf8
  Push-Location $dir
  git init -q -b $branch
  git config user.name "github-actions[bot]"
  git config user.email "41898299+github-actions[bot]@users.noreply.github.com"
  git config core.autocrlf false
  git add -A
  git commit -q -m $message
  git push -q -f $url "HEAD:refs/heads/$branch"
  if ($LASTEXITCODE -ne 0) { Write-Host "Не удалось опубликовать ветку $branch" } else { Write-Host "Опубликована ветка $branch" }
  Pop-Location
}

function New-Stage([string]$name) {
  $d = Join-Path $env:RUNNER_TEMP $name
  if (Test-Path $d) { Remove-Item -Recurse -Force $d }
  New-Item -ItemType Directory -Force $d | Out-Null
  return $d
}

# Журналы
$logs = New-Stage "pub-logs"
if (Test-Path "$root/logs") { Copy-Item -Recurse -Force "$root/logs/*" $logs }
Publish-Branch "ci-logs-build" $logs "CI logs $sha ($Status)"

# Результаты тестов и скриншоты
$ui = New-Stage "pub-ui"
if (Test-Path "$root/ui-results") { Copy-Item -Recurse -Force "$root/ui-results/*" $ui }
Get-ChildItem "$root/logs" -Filter "*tests*.log" -ErrorAction SilentlyContinue | Copy-Item -Destination $ui
Publish-Branch "ui-test-results" $ui "Test results $sha ($Status)"

# Установщик и portable-архив
if ($Status -eq "success") {
  $b = New-Stage "pub-build"
  Copy-Item "$root/artifacts/MDViewer-Setup.exe", "$root/artifacts/MDViewer-portable.zip" $b
  Publish-Branch "build" $b "Build $($env:VERSION) $sha"
}
