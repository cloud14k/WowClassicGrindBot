[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\scripts\set-esp-idf-env.ps1" -Persist

$python = 'C:\Users\cloud\AppData\Local\Programs\Python\Python312\python.exe'
& $python "$env:IDF_PATH\tools\check_python_dependencies.py"
if ($LASTEXITCODE -ne 0) {
    throw 'Global Python dependencies are incomplete.'
}

& "$env:IDF_TOOLS_PATH\tools\idf-exe\1.0.3\idf.py.exe" --version
if ($LASTEXITCODE -ne 0) {
    throw 'idf.py global launcher check failed.'
}
