param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [string]$IndexPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Redpoint/KnowledgeIndex/ai-031-demo/index-v1.json')
)

$ErrorActionPreference = 'Stop'
$redpointRoot = Split-Path -Parent $PSScriptRoot
$targetRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$demoIndex = [IO.Path]::GetFullPath($IndexPath)

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}

Invoke-Dotnet -Arguments @('restore', (Join-Path $redpointRoot 'Redpoint.Codex.Mcp.slnx'))
Invoke-Dotnet -Arguments @('build', (Join-Path $redpointRoot 'Redpoint.Codex.Mcp.slnx'), '--no-restore')
Invoke-Dotnet -Arguments @('run', '--no-build', '--project', (Join-Path $redpointRoot 'tools/Redpoint.KnowledgeIndex'), '--',
    'build', '--repository-root', $targetRoot, '--index-path', $demoIndex, '--provider', 'offline', '--dimensions', '4096')
Invoke-Dotnet -Arguments @('run', '--no-build', '--project', (Join-Path $redpointRoot 'tools/Redpoint.KnowledgeIndex'), '--',
    'validate', '--repository-root', $targetRoot, '--index-path', $demoIndex, '--provider', 'offline', '--dimensions', '4096')

$names = @('REDPOINT_KNOWLEDGE_PROVIDER', 'REDPOINT_KNOWLEDGE_DIMENSIONS', 'REDPOINT_KNOWLEDGE_INDEX_PATH')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:REDPOINT_KNOWLEDGE_PROVIDER = 'offline'
    $env:REDPOINT_KNOWLEDGE_DIMENSIONS = '4096'
    $env:REDPOINT_KNOWLEDGE_INDEX_PATH = $demoIndex
    Invoke-Dotnet -Arguments @('run', '--no-build', '--project', (Join-Path $redpointRoot 'tools/Redpoint.AgentOrchestrator'), '--',
        '--demo', '--knowledge', '--mode', 'analysis', '--repository-root', $targetRoot)
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
