param(
    [string[]]$ExtraRoots = @(),
    [int[]]$SelectIds = @(),
    [switch]$ScanOnly,
    [switch]$Force
)

$ErrorActionPreference = 'SilentlyContinue'

$script:Targets = @()
$script:Selected = @{}
$script:BarLineLen = 0
$script:LogFile = Join-Path $PSScriptRoot 'cleaner-log.csv'
if (-not (Test-Path -LiteralPath $PSScriptRoot)) { $script:LogFile = Join-Path $env:TEMP 'cleaner-log.csv' }

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -lt 1KB) { return "$Bytes B" }
    if ($Bytes -lt 1MB) { return "{0:N0} KB" -f ($Bytes / 1KB) }
    if ($Bytes -lt 1GB) { return "{0:N1} MB" -f ($Bytes / 1MB) }
    return "{0:N2} GB" -f ($Bytes / 1GB)
}

function Get-Impact {
    param([string]$Name)
    switch -Regex ($Name) {
        'recycle'                  { 'PERMANENT - deleted files cannot be restored' }
        'node_modules'             { 'dependencies removed - run npm install / yarn to restore (needs lockfile + internet)' }
        '^npm cache|^pnpm store|^yarn cache|^bun cache' { 'safe - packages re-download on next install' }
        '^pip cache|^uv cache|^composer' { 'safe - re-downloaded on next install' }
        '^cargo registry'          { 'crates re-fetched on next build - offline builds fail until then' }
        '^nuget packages'          { 'restored on next dotnet restore/build - offline builds fail until then' }
        '^gradle'                  { 're-downloaded on next Gradle build' }
        '^maven'                   { 're-downloaded on next Maven build' }
        '^go build cache'          { 'safe - rebuilds automatically' }
        'JetBrains'                { 'safe - rebuilt when the IDE starts' }
        '^\.next|^\.nuxt|^\.output|^\.turbo' { 'build output - recreate with next build / nuxt build; deployed sites need rebuild + redeploy' }
        '^(dist|build|out|target|obj)' { 'build output - regenerated on next project build' }
        '__pycache__|pytest_cache|mypy_cache|ruff_cache' { 'safe - Python regenerates automatically' }
        '^\.cache'                 { 'safe - tools rebuild it automatically' }
        'temp files'               { 'in-use files are skipped; close running apps first' }
        default                    { 'safe - regenerable' }
    }
}

function Show-ProgressBar {
    param([double]$Percent, [string]$Label = '', [string]$Color = 'Cyan')
    $width = 40
    $filled = [Math]::Floor([Math]::Min(100, $Percent) / 100 * $width)
    $bar = '[' + ('#' * $filled) + ('-' * ($width - $filled)) + ']'
    $pct = ("{0,3}%" -f [Math]::Min(100, [int]$Percent))
    $msg = "  $bar $pct  $Label"
    $pad = ''
    if ($msg.Length -lt $script:BarLineLen) { $pad = ' ' * ($script:BarLineLen - $msg.Length) }
    Write-Host ("`r" + $msg + $pad) -NoNewline -ForegroundColor $Color
    $script:BarLineLen = $msg.Length
}

$script:SizeCode = @'
function Get-FolderSize {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    $dest = Join-Path $env:TEMP 'pc-cleaner-null'
    try {
        $out = & robocopy $Path $dest /L /E /BYTES /XJ /NJH /NFL /NDL /NP /NC /NS /R:0 /W:0 2>$null
        $m = $out | Select-String -Pattern 'Bytes\s*:\s*(\d+)' | Select-Object -First 1
        if ($m -and $m.Matches[0].Groups[1].Value) {
            return [long]$m.Matches[0].Groups[1].Value
        }
    } catch {}
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    return [long]$sum
}
'@
Invoke-Expression $script:SizeCode

function Add-Target {
    param(
        [string]$Name,
        [string]$Category,
        [string]$Path,
        [long]$SizeBytes = -1
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $norm = $Path.ToLowerInvariant()
    if ($script:Targets.Path.ToLowerInvariant() -contains $norm) { return }
    $script:Targets += [pscustomobject]@{
        Id         = $script:Targets.Count
        Name       = $Name
        Category   = $Category
        Path       = $Path
        SizeBytes  = $SizeBytes
        Impact     = (Get-Impact -Name $Name)
    }
}

function Compute-AllSizes {
    $pending = $script:Targets | Where-Object { $_.SizeBytes -lt 0 }
    if ($pending.Count -eq 0) { return }
    $pool = $null
    try {
        $pool = [runspacefactory]::CreateRunspacePool(1, 4)
        $pool.Open()
    } catch {
        foreach ($t in $pending) {
            $t.SizeBytes = Get-FolderSize -Path $t.Path
        }
        return
    }
    $jobs = @()
    $catTotal = @{}
    foreach ($t in $pending) {
        $ps = [powershell]::Create()
        $ps.RunspacePool = $pool
        $code = $script:SizeCode + "`r`nGet-FolderSize -Path '" + $t.Path.Replace("'", "''") + "'"
        $ps.AddScript($code) | Out-Null
        $jobs += [pscustomobject]@{ Path = $t.Path; Category = $t.Category; PS = $ps; Handle = $ps.BeginInvoke(); Complete = $false }
        if ($catTotal.ContainsKey($t.Category)) { $catTotal[$t.Category]++ } else { $catTotal[$t.Category] = 1 }
    }
    $catDone = @{}
    $curCat = $pending[0].Category
    $done = 0
    while ($done -lt $jobs.Count) {
        foreach ($j in $jobs) {
            if ($j.Complete) { continue }
            if ($j.Handle.AsyncWaitHandle.WaitOne(0)) {
                $j.Complete = $true
                $done++
                $curCat = $j.Category
                if ($catDone.ContainsKey($curCat)) { $catDone[$curCat]++ } else { $catDone[$curCat] = 1 }
                $size = 0
                try {
                    $raw = $j.PS.EndInvoke($j.Handle)
                    if ($raw -and $raw.Count -gt 0) { $size = [long]$raw[0] }
                } catch {}
                try { $j.PS.Dispose() } catch {}
                $match = $script:Targets | Where-Object { $_.Path -eq $j.Path }
                if ($match) { $match.SizeBytes = $size }
            }
        }
        Show-ProgressBar -Percent (($done / $jobs.Count) * 100) -Label ("Measuring [{0}] {1}/{2}" -f $curCat, $catDone[$curCat], $catTotal[$curCat]) -Color 'Cyan'
        Start-Sleep -Milliseconds 200
    }
    Write-Host ""
    $pool.Close()
    $pool.Dispose()
}

function Add-WildcardTargets {
    param(
        [string]$Root,
        [int]$Depth,
        [int]$MaxDepth,
        [string[]]$TargetNames,
        [string[]]$PruneNames
    )
    if ($Depth -gt $MaxDepth -or -not (Test-Path -LiteralPath $Root)) { return }
    $items = Get-ChildItem -LiteralPath $Root -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 }
    foreach ($d in $items) {
        $n = $d.Name
        if ($PruneNames -contains $n) { continue }
        if ($TargetNames -contains $n) {
            Add-Target -Name $n -Category 'Build output' -Path $d.FullName
            continue
        }
        Add-WildcardTargets -Root $d.FullName -Depth ($Depth + 1) -MaxDepth $MaxDepth -TargetNames $TargetNames -PruneNames $PruneNames
    }
}

function Add-ExplicitCaches {
    $cache = @(
        @{ N = 'npm cache';      P = (Join-Path $env:LOCALAPPDATA 'npm-cache') },
        @{ N = 'pnpm store';     P = (Join-Path $env:LOCALAPPDATA 'pnpm\store') },
        @{ N = 'pip cache';      P = (Join-Path $env:LOCALAPPDATA 'pip\cache') },
        @{ N = 'yarn cache';     P = (Join-Path $env:LOCALAPPDATA 'Yarn') },
        @{ N = 'go build cache'; P = (Join-Path $env:LOCALAPPDATA 'go-build') },
        @{ N = 'go build cache'; P = $env:GOCACHE },
        @{ N = 'nuget packages'; P = (Join-Path $env:USERPROFILE '.nuget\packages') },
        @{ N = 'gradle caches';  P = (Join-Path $env:USERPROFILE '.gradle\caches') },
        @{ N = 'maven repository'; P = (Join-Path $env:USERPROFILE '.m2\repository') },
        @{ N = 'cargo registry'; P = (Join-Path $env:USERPROFILE '.cargo\registry') },
        @{ N = 'bun cache';      P = (Join-Path $env:USERPROFILE '.bun\install\cache') },
        @{ N = 'composer cache'; P = (Join-Path $env:LOCALAPPDATA 'Composer\cache') },
        @{ N = 'composer cache'; P = (Join-Path $env:APPDATA 'Composer\cache') },
        @{ N = 'uv cache';       P = (Join-Path $env:LOCALAPPDATA 'uv\cache') }
    )
    foreach ($c in $cache) {
        if (Test-Path -LiteralPath $c.P) { Add-Target -Name $c.N -Category 'Dev cache' -Path $c.P }
    }
    $jb = Join-Path $env:LOCALAPPDATA 'JetBrains'
    if (Test-Path -LiteralPath $jb) {
        Get-ChildItem -LiteralPath $jb -Directory -Force |
            Where-Object { $_.Name -notlike '*Toolbox*' } |
            ForEach-Object {
                $cc = Join-Path $_.FullName 'caches'
                if (Test-Path -LiteralPath $cc) { Add-Target -Name "JetBrains caches ($($_.Name))" -Category 'Dev cache' -Path $cc }
            }
    }
}

function Get-RecycleBinInfo {
    try {
        $shell = New-Object -ComObject Shell.Application
        $rb = $shell.Namespace(0xA)
        $total = 0L
        $count = 0
        foreach ($it in $rb.Items()) {
            $s = $it.ExtendedProperty('Size')
            if ($s) { $total += [long]$s }
            $count++
        }
        return @{ Count = $count; Size = $total }
    } catch {
        return @{ Count = -1; Size = -1 }
    }
}

function Get-DriveRoots {
    return [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object { $_.RootDirectory.FullName }
}

function Test-ProtectedPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $true }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\').ToLowerInvariant()
    $protected = @()
    foreach ($r in Get-DriveRoots) { $protected += $r.TrimEnd('\').ToLowerInvariant() }
    $protected += $env:USERPROFILE.TrimEnd('\').ToLowerInvariant()
    $protected += $env:ProgramFiles.TrimEnd('\').ToLowerInvariant()
    $protected += ${env:ProgramFiles(x86)}.TrimEnd('\').ToLowerInvariant()
    $protected += $env:SystemRoot.TrimEnd('\').ToLowerInvariant()
    $protected += $env:ProgramData.TrimEnd('\').ToLowerInvariant()
    $protected += $env:LOCALAPPDATA.TrimEnd('\').ToLowerInvariant()
    $protected += $env:APPDATA.TrimEnd('\').ToLowerInvariant()
    foreach ($p in $protected) {
        if ($full -eq $p) { return $true }
    }
    $toolDirs = @()
    foreach ($tool in @('node', 'npm', 'npx', 'yarn', 'pnpm', 'pip', 'python', 'go', 'cargo', 'dotnet', 'java')) {
        $cmd = Get-Command $tool -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source) {
            $dir = Split-Path $cmd.Source -Parent
            $toolDirs += $dir.TrimEnd('\').ToLowerInvariant()
        }
    }
    foreach ($p in $toolDirs) {
        if ($full -eq $p -or $full.StartsWith($p + '\')) { return $true }
    }
    return $false
}

function Read-Selection {
    param([int]$MaxId)
    while ($true) {
        Write-Host ""
        $input = (Read-Host "Enter numbers to toggle (ex: 1,3-5) | A=all | D=done | Q=quit").Trim()
        if ($input -eq '') { continue }
        switch ($input.ToLower()) {
            'a' { for ($i = 0; $i -lt $script:Targets.Count; $i++) { $script:Selected[$i] = $true }; return }
            'd' { return }
            'q' { exit 0 }
            'all' { for ($i = 0; $i -lt $script:Targets.Count; $i++) { $script:Selected[$i] = $true }; return }
            'done' { return }
            'quit' { exit 0 }
        }
        foreach ($token in ($input -split ',')) {
            $token = $token.Trim()
            if ($token -match '^(\d+)-(\d+)$') {
                $lo = [int]$matches[1]; $hi = [int]$matches[2]
                for ($i = $lo; $i -le $hi -and $i -lt $script:Targets.Count; $i++) { $script:Selected[$i] = $true }
            } elseif ($token -match '^\d+$') {
                $i = [int]$token
                if ($i -ge 0 -and $i -lt $script:Targets.Count) {
                    if ($script:Selected.ContainsKey($i)) { $script:Selected.Remove($i) } else { $script:Selected[$i] = $true }
                } else {
                    Write-Host "  Invalid id: $i" -ForegroundColor Red
                }
            }
        }
    }
}

function Write-Log {
    param([string]$Category, [string]$Path, [long]$Size, [string]$Status, [string]$Details)
    $row = [pscustomobject]@{
        Timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        Category  = $Category
        Path      = $Path
        SizeBytes = $Size
        Status    = $Status
        Details   = $Details
    }
    try {
        $row | Export-Csv -LiteralPath $script:LogFile -Append -NoTypeInformation -Encoding UTF8
    } catch {}
}

Clear-Host
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  PC Cleaner - Dev Dependencies, Caches & Temp Files" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Scans only CACHES, DEPENDENCIES and BUILD OUTPUT." -ForegroundColor Yellow
Write-Host "  Installed tools (Node.js, Next.js, databases, etc.)" -ForegroundColor Yellow
Write-Host "  are NEVER touched. Every item is verified with you." -ForegroundColor Yellow
Write-Host "=====================================================" -ForegroundColor Cyan

$targetNames = @('node_modules', '__pycache__', '.pytest_cache', '.mypy_cache', '.ruff_cache', 'dist', 'build', 'out', 'target', 'obj', '.next', '.nuxt', '.output', '.turbo', '.cache')
$pruneNames = @('AppData', '.git', '$Recycle.Bin', 'System Volume Information', 'Recovery', 'PerfLogs', 'Windows.old', '.vscode', '.cursor', '.antigravity-ide', '.idea', 'xampp', 'wamp', 'wamp64', 'laragon', 'phpmyadmin', 'mysql', 'mariadb', 'postgres', 'mongodb', 'redis', 'nginx', 'apache', 'tomcat', 'jenkins', 'docker', 'flutter', 'android', 'jdk', 'jre', 'nodejs', 'tools', 'extensions')

Write-Host ""
Write-Host "[1/4] Scanning dev caches..." -ForegroundColor Cyan
Add-ExplicitCaches

Write-Host "[2/4] Scanning user profile for dependencies & build dirs..." -ForegroundColor Cyan
Add-WildcardTargets -Root $env:USERPROFILE -Depth 0 -MaxDepth 4 -TargetNames $targetNames -PruneNames $pruneNames

Write-Host "[3/4] Scanning drives for dependencies & build dirs..." -ForegroundColor Cyan
$systemDirs = @('Windows', 'Program Files', 'Program Files (x86)', 'ProgramData', '$Recycle.Bin', 'System Volume Information', 'Recovery', 'PerfLogs', 'Users', 'Windows.old')
foreach ($root in Get-DriveRoots) {
    $level1 = Get-ChildItem -LiteralPath $root -Directory -Force |
        Where-Object { $systemDirs -notcontains $_.Name -and ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 }
    foreach ($d in $level1) {
        Add-WildcardTargets -Root $d.FullName -Depth 1 -MaxDepth 3 -TargetNames $targetNames -PruneNames $pruneNames
    }
}

foreach ($root in $ExtraRoots) {
    Add-WildcardTargets -Root $root -Depth 0 -MaxDepth 4 -TargetNames $targetNames -PruneNames $pruneNames
}

Write-Host "[4/4] Checking temp files and recycle bin..." -ForegroundColor Cyan
Add-Target -Name 'User temp files' -Category 'Temp files' -Path $env:TEMP
$winTemp = Join-Path $env:SystemRoot 'Temp'
if (Test-Path -LiteralPath $winTemp) { Add-Target -Name 'Windows temp files' -Category 'Temp files' -Path $winTemp }
$rbInfo = Get-RecycleBinInfo
if ($rbInfo.Count -gt 0) {
    Add-Target -Name 'Recycle Bin' -Category 'Recycle Bin' -Path 'RECYCLEBIN' -SizeBytes $rbInfo.Size
}

Write-Host "Measuring folder sizes (parallel)..." -ForegroundColor Cyan
Compute-AllSizes

$script:Targets = $script:Targets | Where-Object { $_.SizeBytes -gt 0 } | Sort-Object Category, SizeBytes -Descending
for ($i = 0; $i -lt $script:Targets.Count; $i++) { $script:Targets[$i].Id = $i }

if ($script:Targets.Count -eq 0) {
    Write-Host ""
    Write-Host "Nothing to clean. Your dev caches are already empty." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "----------------- SCAN RESULTS -----------------" -ForegroundColor Cyan
Write-Host "  [!] = needs rebuild/reinstall     [!!] = permanent deletion" -ForegroundColor DarkGray
$catWarn = @{
    'Build output' = 'Warning: build outputs - projects that run or deploy from these (next start, served dist) need a rebuild + redeploy.'
    'Dev cache'    = 'Effect: packages re-download on next install - first install is slower, offline builds may fail.'
    'Temp files'   = 'Effect: in-use files are skipped automatically. Close running apps for best results.'
    'Recycle Bin'  = 'Warning: deletion is permanent - files cannot be restored.'
}
$currentCat = ''
$grandTotal = 0L
foreach ($t in $script:Targets) {
    if ($t.Category -ne $currentCat) {
        $currentCat = $t.Category
        Write-Host ""
        Write-Host "  [$currentCat]" -ForegroundColor Magenta
        if ($catWarn.ContainsKey($currentCat)) {
            Write-Host ("    {0}" -f $catWarn[$currentCat]) -ForegroundColor DarkYellow
        }
    }
    $sizeStr = if ($t.SizeBytes -ge 0) { Format-Size -Bytes $t.SizeBytes } else { 'n/a' }
    Write-Host ("  {0,3}) {1,12}  {2,-24} {3}" -f $t.Id, $sizeStr, $t.Name, $t.Path) -ForegroundColor White -NoNewline
    if ($t.Impact -like 'PERMANENT*') {
        Write-Host "  [!!]" -ForegroundColor Red
    } elseif ($t.Impact -notlike 'safe*' -and $t.Impact -notlike 'in-use*') {
        Write-Host "  [!]" -ForegroundColor Yellow
    } else {
        Write-Host ""
    }
    $grandTotal += [Math]::Max(0, $t.SizeBytes)
}
Write-Host ""
Write-Host ("Total reclaimable: {0}" -f (Format-Size -Bytes $grandTotal)) -ForegroundColor Yellow

if ($ScanOnly) {
    Write-Host "Scan-only mode (no changes made)." -ForegroundColor Green
    exit 0
}

if ($SelectIds.Count -gt 0) {
    foreach ($id in $SelectIds) {
        if ($id -ge 0 -and $id -lt $script:Targets.Count) { $script:Selected[$id] = $true }
    }
    if (-not $Force) {
        $confirm = Read-Host ("Delete the {0} selected item(s) now? (y=yes, q=quit)" -f $script:Selected.Count)
        if ($confirm.ToLower() -ne 'y') { exit 0 }
    }
} else {
    $done = $false
    while (-not $done) {
        Read-Selection -MaxId $script:Targets.Count
        $sel = $script:Targets | Where-Object { $script:Selected.ContainsKey($_.Id) }
        if ($sel.Count -eq 0) {
            Write-Host "Nothing selected yet." -ForegroundColor Yellow
            continue
        }
        $selTotal = ($sel | Measure-Object -Property SizeBytes -Sum).Sum
        Write-Host ""
        Write-Host ("Selected {0} item(s), total {1}:" -f $sel.Count, (Format-Size -Bytes $selTotal)) -ForegroundColor Yellow
        foreach ($t in $sel) {
            Write-Host ("  {0,3}) {1,12}  {2,-24} {3}" -f $t.Id, (Format-Size -Bytes $t.SizeBytes), $t.Name, $t.Path) -ForegroundColor White
            Write-Host ("         Impact: {0}" -f $t.Impact) -ForegroundColor DarkYellow
        }
        $confirm = Read-Host "Delete these items now? (y=yes, n=change selection, q=quit)"
        if ($confirm.ToLower() -eq 'y' -or $Force) {
            $done = $true
        } elseif ($confirm.ToLower() -eq 'q') {
            exit 0
        }
    }
}

$freed = 0L
$failed = 0
$selList = @($script:Targets | Where-Object { $script:Selected.ContainsKey($_.Id) })
$cleanTotal = @{}
foreach ($t in $selList) {
    if ($cleanTotal.ContainsKey($t.Category)) { $cleanTotal[$t.Category]++ } else { $cleanTotal[$t.Category] = 1 }
}
$cleanDone = @{}
$lastCleanCat = ''
Write-Host ""
Write-Host "----------------- CLEANING -----------------" -ForegroundColor Cyan
$n = 0
foreach ($t in $selList) {
    $n++
    $size = [Math]::Max(0, $t.SizeBytes)
    if ($t.Category -ne $lastCleanCat) {
        $lastCleanCat = $t.Category
        Write-Host ""
        Write-Host ("--- Cleaning [ {0} ] ---" -f $t.Category) -ForegroundColor Magenta
    }
    Write-Host ("    {0}  ({1})" -f $t.Name, (Format-Size -Bytes $size)) -ForegroundColor White
    Write-Host ("    Impact: {0}" -f $t.Impact) -ForegroundColor DarkYellow
    if ($t.Path -eq 'RECYCLEBIN') {
        foreach ($root in Get-DriveRoots) {
            $letter = $root.Substring(0, 1)
            Clear-RecycleBin -DriveLetter $letter -Force -ErrorAction SilentlyContinue
        }
        $status = 'Deleted'
        $details = 'Recycle Bin emptied'
        $freed += $size
        Write-Host ("  [OK]   {0,12}  {1}" -f (Format-Size -Bytes $size), $t.Name) -ForegroundColor Green
    } else {
        if (Test-ProtectedPath -Path $t.Path) {
            $status = 'Skipped'
            $details = 'Protected path (tool/software directory)'
            $failed++
            Write-Host ("  [SKIP] {0,12}  {1}  <- protected" -f (Format-Size -Bytes $size), $t.Name) -ForegroundColor Red
        } else {
            Remove-Item -LiteralPath $t.Path -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $t.Path) {
                $status = 'Partial'
                $details = 'Some files locked/in use, leftovers remain'
                $failed++
                Write-Host ("  [LOCK] {0,12}  {1}  <- partially locked, retry later" -f (Format-Size -Bytes $size), $t.Name) -ForegroundColor Yellow
            } else {
                $status = 'Deleted'
                $details = 'OK'
                $freed += $size
                Write-Host ("  [OK]   {0,12}  {1}" -f (Format-Size -Bytes $size), $t.Name) -ForegroundColor Green
            }
        }
    }
    if ($cleanDone.ContainsKey($t.Category)) { $cleanDone[$t.Category]++ } else { $cleanDone[$t.Category] = 1 }
    Show-ProgressBar -Percent (($n / $selList.Count) * 100) -Label ("Cleaning [{0}] {1}/{2}   (item {3}/{4})" -f $t.Category, $cleanDone[$t.Category], $cleanTotal[$t.Category], $n, $selList.Count) -Color 'Cyan'
    Write-Log -Category $t.Category -Path $t.Path -Size $size -Status $status -Details $details
}
Write-Host ""

Write-Host ""
Write-Host "----------------- SUMMARY -----------------" -ForegroundColor Cyan
Write-Host ("Freed:   {0}" -f (Format-Size -Bytes $freed)) -ForegroundColor Green
if ($failed -gt 0) {
    Write-Host ("Failed / locked items: {0} (close running programs and retry)" -f $failed) -ForegroundColor Red
} else {
    Write-Host "All items cleaned successfully." -ForegroundColor Green
}
$deletedNonSafe = @($selList | Where-Object { $script:Selected.ContainsKey($_.Id) -and $_.Impact -notlike 'safe*' -and $_.Impact -notlike 'PERMANENT*' -and $_.Impact -notlike 'in-use*' })
if ($deletedNonSafe.Count -gt 0) {
    Write-Host "  ! Rebuild/reinstall these before running their projects:" -ForegroundColor Yellow
    foreach ($t in $deletedNonSafe) {
        Write-Host ("      - {0}  ({1})" -f $t.Path, $t.Impact) -ForegroundColor DarkYellow
    }
}
Write-Host ("Log written to: {0}" -f $script:LogFile) -ForegroundColor DarkGray
Write-Host ([char]7)
Write-Host ""