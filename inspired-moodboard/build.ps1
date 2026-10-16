# Builds the standalone Windows installer: dist\Inspired Moodboard V.1 Setup.exe
#
# Steps
#   1. Downloads the official Electron engine once (cached in .build\) and checks its SHA-256.
#   2. Puts the engine and the app\ folder together, the way they'll be installed.
#   3. Zips that into the installer's payload and records its fingerprint.
#   4. Compiles installer\Setup.cs twice with the C# compiler built into Windows:
#      a small uninstaller, then the full Setup with everything packed inside.
#
# Needs: Windows 10/11 and internet the first time. Nothing to install.
param([string]$Mode = "")
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Engine  = "38.8.6"
$Sha     = "366ae2b4aa9e6bc89b98c8b5831d46303e45cd49b2970c2b3921de5f2fcfc2e1"
$Url     = "https://github.com/electron/electron/releases/download/v$Engine/electron-v$Engine-win32-x64.zip"
$Work    = Join-Path $Root ".build"
$Dist    = Join-Path $Root "dist"
$Stage   = Join-Path $Work "payload"
$Inst    = Join-Path $Root "installer"
$Csc     = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $Csc)) { $Csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }

function Step($t) { Write-Host ""; Write-Host "  $t" -ForegroundColor Red }
function Hash($f) { (Get-FileHash $f -Algorithm SHA256).Hash.ToLower() }
New-Item -ItemType Directory -Force -Path $Work, $Dist | Out-Null

# 1. Engine -------------------------------------------------------------------
$zip = Join-Path $Work "electron-v$Engine-win32-x64.zip"
if (-not (Test-Path $zip) -or (Hash $zip) -ne $Sha) {
  Step "Downloading the app engine (Electron $Engine, about 130 MB, only once)"
  Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
  if ((Hash $zip) -ne $Sha) { Remove-Item $zip -Force; throw "The download didn't match the official Electron file." }
}

# 2. Stage the app exactly as it will be installed ----------------------------
Step "Putting the app together"
# close a copy started earlier with "build.cmd run", or its files stay locked
Get-Process "Inspired Moodboard" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Stage) } | Stop-Process -Force
Start-Sleep -Milliseconds 300
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $Stage -Force
Rename-Item (Join-Path $Stage "electron.exe") "Inspired Moodboard.exe"
Remove-Item (Join-Path $Stage "resources\default_app.asar") -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $Stage "locales") -Filter *.pak | Where-Object { $_.Name -notin @("en-US.pak", "en-GB.pak") } | Remove-Item -Force
Copy-Item (Join-Path $Root "app") (Join-Path $Stage "resources\app") -Recurse
Remove-Item (Join-Path $Stage "resources\app\node_modules") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Inst "res\icon.ico") (Join-Path $Stage "icon.ico")
Set-Content -Path (Join-Path $Stage "engine-$Engine.txt") -Value $Engine -NoNewline

if ($Mode -eq "run") {
  Step "Opening the app (from .build\payload, nothing installed)"
  Start-Process (Join-Path $Stage "Inspired Moodboard.exe")
}

# 3. Payload -------------------------------------------------------------------
Step "Packing the payload"
$payload = Join-Path $Work "payload.zip"
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($Stage, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)
$shaFile = Join-Path $Work "payload.sha256"
[IO.File]::WriteAllText($shaFile, (Hash $payload))

# 4. Compile -------------------------------------------------------------------
Step "Compiling the installer"
$common = @("/nologo", "/target:winexe", "/optimize+", "/platform:anycpu",
  "/win32icon:$Inst\res\icon.ico", "/win32manifest:$Inst\app.manifest",
  "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll")
foreach ($f in Get-ChildItem (Join-Path $Inst "res") -File) { $common += "/resource:$($f.FullName),$($f.Name)" }
foreach ($f in Get-ChildItem (Join-Path $Inst "res\designs") -Filter *.jpg) { $common += "/resource:$($f.FullName),$($f.Name)" }

$uninst = Join-Path $Work "uninstaller.exe"
& $Csc @common "/out:$uninst" (Join-Path $Inst "Setup.cs")
if ($LASTEXITCODE -ne 0) { throw "The uninstaller didn't compile (see the errors above)." }

$setup = Join-Path $Dist "Inspired Moodboard V.1 Setup.exe"
& $Csc @common "/resource:$uninst,uninstaller.exe" "/resource:$payload,payload.zip" "/resource:$shaFile,payload.sha256" "/out:$setup" (Join-Path $Inst "Setup.cs")
if ($LASTEXITCODE -ne 0) { throw "Setup didn't compile (see the errors above)." }

$mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Step "Done: dist\Inspired Moodboard V.1 Setup.exe ($mb MB)"
Write-Host "  SHA-256 $(Hash $setup)"
