[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'EmailApprovalServer\EmailApprovalServer.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must be a three-part version.' }
$packageName = 'EmailApprovalServer-{0}-v{1}-{2}' -f (Get-Date -Format 'yyyyMMdd'), $version, (Get-Date -Format 'HHmmss')
$publishRoot = Join-Path $projectRoot 'publish'
$packageFolder = Join-Path $publishRoot $packageName
$zipFile = Join-Path $publishRoot ($packageName + '.zip')
if ((Test-Path -LiteralPath $packageFolder) -or (Test-Path -LiteralPath $zipFile)) { throw 'Package already exists. Retry in a new second.' }

function Invoke-DotNet {
    param([string[]]$CommandArgs)
    & dotnet @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($CommandArgs[0]) failed with exit code $LASTEXITCODE. No release ZIP was created." }
}

Push-Location $projectRoot
try {
    # 실제 SAP 접속이나 이메일 발송 없이 계약 테스트를 실행한다.
    Invoke-DotNet -CommandArgs @('restore', 'EmailApprovalServer.ContractTests', '--configfile', (Join-Path $projectRoot 'NuGet.Config'))
    Invoke-DotNet -CommandArgs @('run', '--project', 'EmailApprovalServer.ContractTests', '-c', 'Release', '--no-restore')
    Invoke-DotNet -CommandArgs @('publish', $projectFile, '-c', 'Release', '--no-restore', '-o', $packageFolder)
    Copy-Item -LiteralPath (Join-Path $projectRoot 'DEPLOYMENT.md') -Destination $packageFolder
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\APPROVAL_HISTORY.md') -Destination $packageFolder
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\INTEGRATION_20260919.md') -Destination $packageFolder
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\INTEGRATION_20260921.md') -Destination $packageFolder
    $assembly = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $packageFolder 'EmailApprovalServer.dll'))
    if ($assembly.Version.ToString(3) -ne $version) { throw 'Published assembly version does not match the project version.' }
    Compress-Archive -Path (Join-Path $packageFolder '*') -DestinationPath $zipFile
    $hash = (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash
    Set-Content -LiteralPath ($zipFile + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($zipFile)) -Encoding ascii
    Write-Output "Release ZIP: $zipFile"
    Write-Output 'Config/appsettings.xml is included as currently configured. The script does not deploy or start the server.'
}
finally { Pop-Location }
