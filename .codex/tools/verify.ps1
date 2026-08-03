[CmdletBinding()]
param(
    [ValidateSet('Focused', 'Project', 'Full')]
    [string]$Level = 'Focused',
    [string]$TestFilter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KnowledgeTools.psm1') -Force
$root = Get-DataSyncRepositoryRoot

function Invoke-CheckedCommand {
    param([string]$Label, [scriptblock]$Command)
    Write-Output "开始：$Label"
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Label 失败，退出码 $LASTEXITCODE。" }
    Write-Output "通过：$Label"
}

Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'lint-knowledge.ps1')
    if ($LASTEXITCODE -ne 0) { throw '知识校验失败。' }

    if ($Level -eq 'Focused') {
        $changed = @(Get-DataSyncChangedFiles -RepositoryRoot $root)
        $testProjects = [System.Collections.Generic.List[string]]::new()
        $rootBuildChanged = @($changed | Where-Object {
            $_ -eq 'DataSync.sln' -or
            $_ -eq 'global.json' -or
            $_ -eq 'NuGet.config' -or
            $_ -like 'Directory.Build.*' -or
            $_ -eq 'Directory.Packages.props'
        }).Count -gt 0
        if ($rootBuildChanged) {
            Invoke-CheckedCommand '解决方案还原' { & dotnet restore DataSync.sln }
            Invoke-CheckedCommand '解决方案聚焦验证' { & dotnet test DataSync.sln --no-restore --configuration Release -p:UseAppHost=false }
            return
        }
        if ($changed | Where-Object { $_ -like 'DataSync.CYYY/*' -or $_ -like 'DataSync.Common/*' }) {
            $testProjects.Add('DataSync.CYYY.Tests/DataSync.CYYY.Tests.csproj')
        }
        if ($changed | Where-Object { $_ -like 'DataSync.LHYY.V2/*' -or $_ -like 'DataSync.Common/*' }) {
            $testProjects.Add('DataSync.LHYY.V2/DataSync.LHYY.V2.Tests/DataSync.LHYY.V2.Tests.csproj')
        }
        foreach ($project in @($testProjects | Select-Object -Unique)) {
            Invoke-CheckedCommand "还原 $project" { & dotnet restore $project }
            $arguments = @('test', $project, '--no-restore', '--configuration', 'Release', '-p:UseAppHost=false')
            if ($TestFilter) { $arguments += @('--filter', $TestFilter) }
            Invoke-CheckedCommand "聚焦测试 $project" { & dotnet @arguments }
        }
        if ($testProjects.Count -eq 0) { Write-Output '当前仅有治理文件变化，Focused 不运行产品测试。' }
        return
    }

    Invoke-CheckedCommand '解决方案还原' { & dotnet restore DataSync.sln }
    Invoke-CheckedCommand '解决方案测试' { & dotnet test DataSync.sln --no-restore --configuration Release -p:UseAppHost=false }
    if ($Level -eq 'Full') {
        Invoke-CheckedCommand 'Release 解决方案构建' { & dotnet build DataSync.sln --no-restore --configuration Release -p:UseAppHost=false }
    }
}
finally {
    Pop-Location
}
