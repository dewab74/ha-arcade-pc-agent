[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

if (-not $Path -or $Path.Count -eq 0) {
    exit 0
}

$failed = $false

foreach ($file in $Path) {
    $resolved = Resolve-Path -LiteralPath $file -ErrorAction Stop
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $resolved.Path,
        [ref]$tokens,
        [ref]$errors
    )

    foreach ($error in $errors) {
        $failed = $true
        $line = $error.Extent.StartLineNumber
        $column = $error.Extent.StartColumnNumber
        Write-Error ("{0}:{1}:{2} {3}" -f $resolved.Path, $line, $column, $error.Message)
    }
}

if ($failed) {
    exit 1
}
