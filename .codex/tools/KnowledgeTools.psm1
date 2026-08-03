Set-StrictMode -Version Latest

$script:DataSyncSecretPattern = '(?im)(-----BEGIN (?:RSA |OPENSSH |EC |DSA |ENCRYPTED )?PRIVATE KEY-----|\b(?:password|passwd|pwd|api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|bearer[_-]?token)\b\s*[:=]\s*["'']?[^\s"''<>{}]+|postgres(?:ql)?://[^\s]+:[^\s]+@|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b|\bgithub_pat_[A-Za-z0-9_]{10,}\b)'

function Get-DataSyncRepositoryRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
}

function Get-DataSyncKnowledgeMap {
    param([string]$RepositoryRoot = (Get-DataSyncRepositoryRoot))

    $mapPath = Join-Path $RepositoryRoot '.agents/knowledge-map.yaml'
    if (-not (Test-Path -LiteralPath $mapPath -PathType Leaf)) {
        throw "知识映射不存在：$mapPath"
    }

    try {
        return Get-Content -LiteralPath $mapPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "知识映射不是有效的 JSON 兼容 YAML：$($_.Exception.Message)"
    }
}

function ConvertTo-DataSyncRepoPath {
    param([Parameter(Mandatory)][string]$Path)
    $normalized = $Path.Trim().Replace('\', '/')
    if ($normalized.StartsWith('./', [StringComparison]::Ordinal)) {
        return $normalized.Substring(2)
    }
    return $normalized
}

function Test-DataSyncPathPattern {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern
    )

    $normalizedPath = ConvertTo-DataSyncRepoPath $Path
    $normalizedPattern = ConvertTo-DataSyncRepoPath $Pattern
    $placeholder = [char]0x1F
    $regex = [Regex]::Escape($normalizedPattern)
    $regex = $regex.Replace('\*\*', [string]$placeholder)
    $regex = $regex.Replace('\*', '[^/]*').Replace('\?', '[^/]')
    $regex = $regex.Replace([Regex]::Escape([string]$placeholder), '.*')
    return $normalizedPath -match "^$regex$"
}

function Get-DataSyncChangedFiles {
    param(
        [string]$RepositoryRoot = (Get-DataSyncRepositoryRoot),
        [string]$BaseRef
    )

    Push-Location $RepositoryRoot
    try {
        if ($BaseRef) {
            $tracked = @(& git diff --name-only "$BaseRef...HEAD" --)
            if ($LASTEXITCODE -ne 0) { throw "无法比较基准 $BaseRef" }
        }
        else {
            $tracked = @(& git diff --name-only HEAD --)
            if ($LASTEXITCODE -ne 0) { throw '无法读取工作区差异' }
            $untracked = @(& git ls-files --others --exclude-standard)
            if ($LASTEXITCODE -ne 0) { throw '无法读取未跟踪文件' }
            $tracked += $untracked
        }

        return @($tracked | Where-Object { $_ } | ForEach-Object { ConvertTo-DataSyncRepoPath $_ } | Sort-Object -Unique)
    }
    finally {
        Pop-Location
    }
}

function Test-DataSyncSecretText {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    return $Text -match $script:DataSyncSecretPattern
}

Export-ModuleMember -Function Get-DataSyncRepositoryRoot, Get-DataSyncKnowledgeMap, ConvertTo-DataSyncRepoPath, Test-DataSyncPathPattern, Get-DataSyncChangedFiles, Test-DataSyncSecretText
